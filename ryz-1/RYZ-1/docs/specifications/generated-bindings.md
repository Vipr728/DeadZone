# Generated Bindings

Bindings are resolved once in this order: explicit API, public members, serialized members, cached compiled
delegates, generated adapter, manual adapter. Reflection is permitted during discovery and binding, never as an
uncached per-tick mechanism.

The first slice does not generate code because the existing simulator exposes an explicit adapter. Future
generated files are deterministic previews written only after confirmation to `Assets/Ryzi.Generated/`.
Generation never edits customer scripts. Removing the generated folder and package must leave gameplay intact.
