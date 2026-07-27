"""Filesystem scoping and swappable outbound-egress controls."""

from __future__ import annotations

import ipaddress
import os
import socket
from pathlib import Path
from typing import Callable, Protocol, Sequence
from urllib.parse import urlparse

import httpx


class EgressPolicyError(RuntimeError):
    """The requested egress mechanism is unavailable or failed."""


class EgressBlockedError(OSError):
    """An application-boundary connection was actively denied."""


class IEgressPolicy(Protocol):
    def is_read_allowed(self, path: str | Path) -> bool: ...

    def is_write_allowed(self, path: str | Path) -> bool: ...

    def block_egress(self) -> None: ...


class PathEgressPolicy:
    """Canonical containment checks for allowed read and write roots."""

    def __init__(
        self,
        allowed_read_paths: Sequence[Path],
        allowed_write_paths: Sequence[Path],
    ) -> None:
        self.allowed_read_paths = tuple(path.resolve(strict=False) for path in allowed_read_paths)
        self.allowed_write_paths = tuple(
            path.resolve(strict=False) for path in allowed_write_paths
        )

    @staticmethod
    def _allowed(path: str | Path, roots: Sequence[Path]) -> bool:
        candidate = Path(path).expanduser().resolve(strict=False)
        return any(candidate == root or candidate.is_relative_to(root) for root in roots)

    def is_read_allowed(self, path: str | Path) -> bool:
        return self._allowed(path, self.allowed_read_paths)

    def is_write_allowed(self, path: str | Path) -> bool:
        return self._allowed(path, self.allowed_write_paths)

    def block_egress(self) -> None:
        raise NotImplementedError("PathEgressPolicy only validates filesystem paths")


def _is_loopback(host: object) -> bool:
    if not isinstance(host, str):
        return False
    if host.lower() == "localhost":
        return True
    try:
        return ipaddress.ip_address(host.split("%", 1)[0]).is_loopback
    except ValueError:
        return False


class ApplicationEgressPolicy(PathEgressPolicy):
    """Process-local socket guard with an explicit local-model allow-list."""

    def __init__(
        self,
        allowed_read_paths: Sequence[Path],
        allowed_write_paths: Sequence[Path],
        llm_allowlist: Sequence[str] = (),
    ) -> None:
        super().__init__(allowed_read_paths, allowed_write_paths)
        self.llm_allowlist = frozenset(endpoint.lower() for endpoint in llm_allowlist)
        self._original_getaddrinfo = None
        self._original_connect = None

    def block_egress(self) -> None:
        if self._original_connect is not None:
            return
        self._original_getaddrinfo = socket.getaddrinfo
        self._original_connect = socket.socket.connect
        original_getaddrinfo = self._original_getaddrinfo
        original_connect = self._original_connect

        def allowed(host: object, port: object) -> bool:
            if not _is_loopback(host):
                return False
            if not self.llm_allowlist:
                return True
            if not isinstance(host, str) or not isinstance(port, int):
                return False
            return f"{host.lower()}:{port}" in self.llm_allowlist

        def guarded_getaddrinfo(host, *args, **kwargs):
            port = args[0] if args else kwargs.get("port")
            if not allowed(host, port):
                raise EgressBlockedError(f"Outbound DNS/connection blocked for host {host!r}")
            return original_getaddrinfo(host, *args, **kwargs)

        def guarded_connect(sock, address):
            if isinstance(address, tuple) and not allowed(address[0], address[1]):
                raise EgressBlockedError(
                    f"Outbound connection blocked for address {address[0]!r}"
                )
            return original_connect(sock, address)

        socket.getaddrinfo = guarded_getaddrinfo
        socket.socket.connect = guarded_connect

    def restore_egress(self) -> None:
        if self._original_getaddrinfo is not None:
            socket.getaddrinfo = self._original_getaddrinfo
        if self._original_connect is not None:
            socket.socket.connect = self._original_connect
        self._original_getaddrinfo = None
        self._original_connect = None


class NetworkNamespaceEgressPolicy(PathEgressPolicy):
    """Linux current-process network namespace isolation for the GB10 gate."""

    def block_egress(self) -> None:
        unshare = getattr(os, "unshare", None)
        clone_newnet = getattr(os, "CLONE_NEWNET", None)
        if unshare is None or clone_newnet is None:
            raise EgressPolicyError(
                "Network namespaces are unavailable in this Python/OS; use Linux with "
                "permission to call unshare(CLONE_NEWNET)"
            )
        try:
            unshare(clone_newnet)
        except OSError as exc:
            raise EgressPolicyError(f"Could not create a network namespace: {exc}") from exc


class OpenShellEgressPolicy(PathEgressPolicy):
    """Deliberately unavailable until the sponsor API is verified on the GB10."""

    def block_egress(self) -> None:
        raise EgressPolicyError(
            "OpenShell integration is not configured or verified; select `application` "
            "for laptop tests or `network_namespace` for the GB10 validation gate"
        )


def demo_egress_proof(
    policy: IEgressPolicy,
    *,
    llm_endpoint: str | None = None,
    local_probe: Callable[[], bool] | None = None,
) -> bool:
    """Prove external egress is denied while the configured local model remains reachable."""
    policy.block_egress()
    try:
        externally_blocked = False
        try:
            with httpx.Client(timeout=5, trust_env=False) as client:
                client.get("http://example.com/")
        except Exception as exc:
            current: BaseException | None = exc
            while current is not None:
                if isinstance(current, EgressBlockedError):
                    externally_blocked = True
                    break
                current = current.__cause__ or current.__context__
            if not externally_blocked:
                externally_blocked = isinstance(policy, NetworkNamespaceEgressPolicy)
        if not externally_blocked:
            return False
        if local_probe is not None:
            return local_probe()
        if llm_endpoint is not None:
            parsed = urlparse(llm_endpoint)
            if not parsed.scheme or not parsed.hostname or not parsed.port:
                raise EgressPolicyError(f"Invalid local LLM endpoint: {llm_endpoint!r}")
            try:
                with httpx.Client(timeout=5, trust_env=False) as client:
                    response = client.get(f"{llm_endpoint.rstrip('/')}/api/tags")
                    return response.is_success
            except httpx.HTTPError:
                return False
        return externally_blocked
    finally:
        restore = getattr(policy, "restore_egress", None)
        if restore is not None:
            restore()
