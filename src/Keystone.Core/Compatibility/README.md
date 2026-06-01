# Keystone.Core.Compatibility

Polyfills required by C# language features that target newer
runtimes than `netstandard2.1`. Keep this folder small -- if it grows
beyond a few one-liners, move to a dedicated `KeystonePolyfills`
package or rethink the target framework.

## Pieces

| Type | Role |
|---|---|
| `IsExternalInit` | `internal static`. Required by the compiler to allow C# 9+ `init`-only setters when the runtime doesn't ship the type. Pure declaration; no behaviour. |
