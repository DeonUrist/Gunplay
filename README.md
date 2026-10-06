# Gunplay

Independent BepInEx 5 mod for Apocalypter, extracted from Apocaraider 1.7.0. Plugin GUID: `com.denis.apocalypter.gunplay`. Install this mod in place of the gunplay functionality of the combined Apocaraider mod; running the combined mod alongside its replacements would apply the same gameplay twice.

Gunplay owns NPC and player projectile simulation, glowing tracers/crossbow bolts, travel time, weapon range and distance damage falloff, headshot adjustment, shotgun pellets, vehicle damage and penetration, wheel damage/detachment, corpse hit blood/stab audio, and human boss health adjustment. NPCAI owns NPC combat decisions and perception; GunplayHUD owns hit feedback. Both integrations are optional.

## Changes

- 1.1.0: Monster projectiles (Arachnid, Teacher and other gunless ranged attackers) no longer fly through walls. They stop at terrain, walls, items, doors and vehicles, and pass car glass, grids, doors and plates as often as a pistol bullet, damaging the part. Setting: `[Gunplay] MonsterProjectilesHitWalls` (default on).
