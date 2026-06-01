# Keystone.Core.Tests

MSTest unit tests for `Keystone.Core`. Targets `net10.0`; references
the Core project only -- no game DLLs on disk required to run.

## Structure

Subfolders mirror the production namespace layout (`Biomes/`,
`Buildings/`, `Diagnostics/`, `Ecology/Fields/`, `Flora/`,
`Persistence/`, `Regions/`, `Survey/`). When adding a new Core
subsystem, drop tests in the matching folder; when reading existing
tests, the corresponding source README under `src/Keystone.Core/`
explains the design they're pinning.

## Pattern

Arrange / Act / Assert. Tests drive the Core types through hand-rolled
fake ports rather than mocks -- the seam is the port interface itself,
and a one-page hand-roll usually beats a mocking framework on
readability for this kind of pure-data simulation code.

## Coverage

Run `dotnet test --collect:"Code Coverage;Format=cobertura"
--results-directory TestResults` for a Cobertura XML drop -- MSTest
4.x ships the collector built-in, no extra packages needed.
