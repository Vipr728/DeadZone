from __future__ import annotations

import socket

import pytest

from playtester_infra.openshell_policy import (
    ApplicationEgressPolicy,
    EgressBlockedError,
    EgressPolicyError,
    OpenShellEgressPolicy,
    PathEgressPolicy,
    demo_egress_proof,
)


def test_path_policy_allows_only_canonical_descendants(tmp_path):
    readable = tmp_path / "read"
    writable = tmp_path / "write"
    readable.mkdir()
    writable.mkdir()
    policy = PathEgressPolicy([readable], [writable])
    assert policy.is_read_allowed(readable / "nested" / "file.json")
    assert policy.is_write_allowed(writable / "report.json")
    assert not policy.is_read_allowed(tmp_path / "read-sibling" / "secret")
    assert not policy.is_write_allowed(tmp_path / "outside.json")


def test_symlink_escape_is_denied(tmp_path):
    readable = tmp_path / "read"
    outside = tmp_path / "outside"
    readable.mkdir()
    outside.mkdir()
    link = readable / "escape"
    try:
        link.symlink_to(outside, target_is_directory=True)
    except OSError:
        pytest.skip("symlinks unavailable")
    policy = PathEgressPolicy([readable], [readable])
    assert not policy.is_read_allowed(link / "secret.json")


def test_application_policy_actively_blocks_and_then_restores(tmp_path):
    policy = ApplicationEgressPolicy([tmp_path], [tmp_path])
    policy.block_egress()
    try:
        with pytest.raises(EgressBlockedError):
            socket.getaddrinfo("example.com", 80)
        assert socket.getaddrinfo("localhost", 80)
    finally:
        policy.restore_egress()
    assert socket.getaddrinfo("localhost", 80)


def test_demo_passes_only_on_specific_application_block(tmp_path):
    assert demo_egress_proof(ApplicationEgressPolicy([tmp_path], [tmp_path]))
    with pytest.raises(EgressPolicyError, match="not configured or verified"):
        demo_egress_proof(OpenShellEgressPolicy([tmp_path], [tmp_path]))
