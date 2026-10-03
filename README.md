# Gunplay

Independent BepInEx 5 mod for Apocalypter, extracted from Apocaraider 1.7.0. Plugin GUID: `com.denis.apocalypter.gunplay`. Install this mod in place of the gunplay functionality of the combined Apocaraider mod; running the combined mod alongside its replacements would apply the same gameplay twice.

Gunplay owns NPC and player projectile simulation, glowing tracers/crossbow bolts, travel time, weapon range and distance damage falloff, headshot adjustment, shotgun pellets, vehicle damage and penetration, wheel damage/detachment, corpse hit blood/stab audio, and human boss health adjustment. NPCAI owns NPC combat decisions and perception; GunplayHUD owns hit feedback. Both integrations are optional.

## Build and install

Install a .NET SDK that can build .NET Framework 4.7.2 projects. Build against the installed game's own managed assemblies and BepInEx core:

```powershell
dotnet build Gunplay.csproj -c Release -p:GameDir="<Apocalypter installation directory>"
```

The result is `bin/Release/Gunplay.dll`, with `Sounds/knifestab.wav` beneath the same output directory. Copy the DLL and that Sounds directory into `BepInEx/plugins/Gunplay/`. Builds never deploy automatically or modify the installed game. Game, Unity, Harmony and BepInEx assemblies are external references and are not shipped.

## Configuration and ApocaSetter

The config file is `BepInEx/config/com.denis.apocalypter.gunplay.cfg`. `[General] Apocasetter = true` opts into ApocaSetter's ordinary plugin/config discovery. Existing public sections, keys, defaults and meanings are preserved for the owned settings. On first creation, matching public values are imported from the old Apocaraider config; an existing Gunplay config takes precedence and the old file is never changed.

The six formerly hidden `[Tracers] PistolRange`, `SmgRange`, `RifleRange`, `SniperRange`, `ShotgunRange`, and `CrossbowRange` settings are now public configuration entries. Their defaults are 60, 70, 120, 250, 35, and 90 metres. Remaining originally hidden settings remain bound to a config that is never saved. Default full damage continues through 50% of the range, then decreases linearly to zero.

The old `[Gunplay] NpcAim` setting is owned by NPCAI. Gunplay retains projectile-specific lead settings and uses NPCAI's optional spread calculation when present. Alone, its spread calculation preserves the original defaults: base 5 metres, then 10% extra jitter for each full 5 metres beyond the base.

## Optional API

`Gunplay.Api` exposes only CLR/game types and has `ContractVersion = 1`:

- `float EffectiveRange(int kind)` reads authoritative live Gunplay range configuration.
- `bool TryGetWeaponKind(GameObject owner, out int kind)` preserves the original active weapon hierarchy/FSM discovery.
- `bool ProjectilesEnabled` reports whether Gunplay currently replaces ordinary shots.

Stable kind codes are pistol 0, SMG 1, rifle 2, sniper 3, shotgun 4, crossbow 5. Values are clamped to at least one metre; unsupported kinds use rifle range. NPCAI discovers this contract at runtime and otherwise uses its compatible internal fallback. Neither assembly references the other.

Outgoing reflection delegates notify `NPCAI.Api.Shot(GameObject, Vector3, int, bool)` and `Hurt(GameObject, GameObject)`, query `SpreadFactor(float)`, and notify `GunplayHUD.Api.PlayerHit(GameObject, Vector3, float, bool)` / `PartHit(GameObject, Vector3, float)`. Missing APIs and callback exceptions cannot disable the shot simulation. Discovery is retried for late-loading plugins. HUD pending hits are flushed by its own runner.

Apocapatrol's old fitted-wheel ownership probe recognizes only the Apocaraider assembly. An optional Harmony patch redirects its private `MeleeWheels.RaiderHandles` probe to Gunplay, preventing two fitted-wheel damage implementations. Apocapatrol source remains unchanged; this integration depends on that existing private method name and needs in-game verification.

## Verification

Release compilation succeeded using the local game assemblies with zero compiler warnings and errors. Static source review preserves the original projectile, penetration, wheel, corpse and falloff methods, replacing cross-feature calls with optional contracts. Boss discovery now has its own CreateObject hook and independent scene scans, removing its former dependence on NPC detection/navigation helpers. The persistent update runner and scene reset behavior are preserved.

Actual game behavior, all visual/audio effects, optional callback timing, ApocaSetter's live menu and Apocapatrol melee interoperability require in-game verification. No game execution is claimed.
