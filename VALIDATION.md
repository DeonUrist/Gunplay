# Gunplay 1.2.0 validation

The ammo mechanics and serialization were moved from the player-verified Apocaunloader 1.3.0 into Gunplay's assembly.
Gunplay HUD source and package are unchanged.

- 34 logic checks pass, including 15,251 exhaustive overflow count/capacity combinations, independent magazines,
  migration without duplication, chamber state and corrupt data rejection.
- 31 native checks pass inside the shipped Unity player with synthetic PlayMaker weapon FSMs and actual Easy Save I/O.
  They cover 100 identical-gun switches, borrowing slot parents, interrupted unloading, chamber state, save-as, exact save
  slots, recreated/dropped items, old-format saves, rebuilt caches and fault preservation.
- The native checks run both with Gunplay alone and with the installed Apocaunloader 1.3.0 DLL. They verify that the legacy
  hooks are removed, Gunplay owns ammo, all sixteen production patches install, existing Gunplay settings win, and missing
  settings import from Unloader.
- Disabling unloading leaves overflow protection and per-gun tracking active. Disabling projectile simulation leaves ammo
  preservation active.

The native harness redirects disk saves to `verification/output`, uses an isolated plugin/config directory, and never
loads player saves. These checks exercise the native assemblies with synthetic FSMs rather than a full animation playthrough.
