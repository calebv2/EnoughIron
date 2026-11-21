# Enough Iron!
Allows metal items that were stuck with the appearance of iron to be any metal
Must be on client and server
## What does this mod actually do?
Adds the component that any texturable item uses to set its material to the iron items, as well as adding the metal material distribution to them so that they can spawn as different materials in the mines
## What items are included right now?
- All metal handles (Highly recommend using [MetalHandleMoulds](https://github.com/CG-Nik/MetalHandleMoulds) to get the best out of this)
- Large and small metal shields
- The stock Metal Bow

## Metal bows

Put a blank mould and a bare stock Metal Bow into the standard mould press. Pulling the lever consumes the donor bow and leaves a Metal Bow Mould. The standard crafted mould has three uses. Each cast takes 8 ingots of one unlocked metal and produces one whole bow in that metal.

To recycle a bow, remove its attachments and place or drop one bare stock Metal Bow into the smelter's three-slot ore tray with the other ore inputs empty. Add fuel and work the bellows to recover four matching stock ingots. One normal fuel unit is enough. The bow's metal must be unlocked at the smelter. Bows with the shared Copper and Bronze material yield Copper ingots.

Cave loot uses the existing metal distribution. Plain spawns start in Iron, and the game saves the bow's material.

Iron draws at stock speed and Death Steel draws at 8×. Metals above Iron scale linearly by their damage stat up to 8×, putting Mythril at about 4.62×. Copper and Bronze draw at 0.70×, Gold at 0.50×, and Legacy Crystal at 0.85×. Arrow impact damage uses the metal's damage stat relative to Iron multiplied by 0.55, so the Iron bow hits for 55% of the stock bow's damage with the same arrow and draw strength.

Each successful shot wears the bow, including misses. The game saves the wear percentage and applies it to the material's shot limit on reload. These limits assume a full bow and firing wear alone.

| Metal | Shots |
|---|---:|
| Copper, Bronze, Gold | 250 |
| Legacy Crystal | 350 |
| Iron | 500 |
| Red Iron Alloy | 600 |
| Orchi Alloy, White Gold Alloy | 750 |
| Silver | 900 |
| Mythril | 1,200 |
| Evinon Steel Alloy | 1,450 |
| Crysteel | 1,650 |
| Ember Steel | 1,700 |
| Carsi Alloy | 1,800 |
| Pink Diamond | 1,900 |
| Light & Dark Steel | 2,100 |
| Jade Steel | 2,250 |
| Death Steel | 2,500 |

Unlisted materials use Iron's 500-shot limit.

Use the usual server spawn command to select a metal. Replace `YourName` with the player name.

```text
spawn YourName 25100 copper
spawn YourName 25100 mythril
```
