# Simulation

The commercial runtime exposes `IPlaytestGameAdapter`, `UniversalAction`, `UniversalObservation`,
`EpisodeResetContext`, `EpisodeStatus`, events, profiles, and tunables. Universal buttons carry stable channel
IDs plus pressed/held/released edge semantics.

The existing `BeamSearchSolver` and `SegmentedSolver` remain the baseline solver. The integration provider adapts
package requests to the existing semantic `PlayerAction` macros. Segmented solving still carries accumulated
prefixes and performs one fresh-reset replay of the concatenated stream before reporting success.

Bundled profiles are synthetic and neutrally labeled `Constrained`, `Standard`, and `Precision`. They are search
budget configurations, not validated models of people.
