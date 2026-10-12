# Gunplay

Independent BepInEx 5 mod for Apocalypter, extracted from Apocaraider 1.7.0. Plugin GUID: `com.denis.apocalypter.gunplay`. Install this mod in place of the gunplay functionality of the combined Apocaraider mod; running the combined mod alongside its replacements would apply the same gameplay twice.

Gunplay owns NPC and player projectile simulation, glowing tracers/crossbow bolts, travel time, weapon range and distance damage falloff, headshot adjustment, shotgun pellets, vehicle damage and penetration, wheel damage/detachment, corpse hit blood/stab audio, and human boss health adjustment. NPCAI owns NPC combat decisions and perception; GunplayHUD owns hit feedback. Both integrations are optional.

Gunplay 1.2.0 also includes Apocaunloader's ammo features: hold Reload to unload the current gun, preserve overflow when
changing/removing backpacks, and keep independent magazines and hammer/chamber state for identical guns. Overflow becomes
a save-registered ammo item; a failed spawn retains the rounds and retries. Backpack reserve ammo stays shared by caliber.

Existing `Apocaunloader.Magazines` save data is used unchanged, so switching from Apocaunloader 1.3.0 preserves each gun's
rounds. A save without that data assigns the old type's loaded rounds to the first gun drawn of that type; other copies
start empty. The native switching hook also supports Apocapocket's additional slots.

Disable standalone Apocaunloader after updating Gunplay. The mod index marks it deprecated and Gunplay replaces it.
If both DLLs are installed manually, Gunplay disables the old ammo hooks and runner so the features run once.
Unloader settings are imported on first use of each unset Gunplay ammo setting; existing Gunplay values win and the old
config is retained. Gunplay HUD remains a separate optional mod.

## Ammo settings

Config: `BepInEx/config/com.denis.apocalypter.gunplay.cfg`, section `[Ammo]`.

| Key | Default | Behavior |
| --- | --- | --- |
| `EnableUnloading` | `true` | Hold Reload to unload; tap reloads on release |
| `HoldSeconds` | `0.35` | Hold duration in seconds |
| `AnimationTimeout` | `4` | Timeout for each unload animation phase |
| `FallbackKey` | `R` | Used if the Reload input axis is unavailable |
| `DropBackpackOverflow` | `true` | Drop excess ammo on backpack capacity reductions; retry failed drops |

Per-gun magazine tracking remains active while Gunplay is installed, including when unloading or projectile simulation is
disabled, to preserve loaded rounds. `EnableGunplay` continues to control bullet simulation; unloading and backpack
overflow have their own switches. Ammo logs use Gunplay's existing `VerboseLog` setting.

## Verification

`dotnet run --project verification/LogicTests.csproj -c Release` checks overflow conservation and per-item magazine logic.
`./verification/RunNative.ps1` checks synthetic weapon FSMs inside an isolated Unity/PlayMaker/Easy Save process.
`./verification/RunNative.ps1 -WithLegacy` repeats those checks with the installed Apocaunloader DLL and verifies takeover.
The native harness redirects saves into `verification/output` and does not load player saves.

## Changes

- 1.2.0: Unloading, backpack overflow protection and independent per-gun magazines integrated from Apocaunloader. Existing saves/settings carry over; duplicate standalone hooks are suppressed.
- 1.1.0: Monster projectiles (Arachnid, Teacher and other gunless ranged attackers) no longer fly through walls. They stop at terrain, walls, items, doors and vehicles, and pass car glass, grids, doors and plates as often as a pistol bullet, damaging the part. Setting: `[Gunplay] MonsterProjectilesHitWalls` (default on).
