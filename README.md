# AI Basketball

Neuroevolution in Godot (C#). Forty bots
evolve to fetch a basketball and shoot basket in their own cells. No physics engine is involved in training:
bot movement and ball physics (gravity, bounces, backboard) are simulated by a small custom
loop so generations run hundreds of times faster than real time.

## Requirements

- Godot 4.7.2 **.NET** build (not the standard build)
- .NET SDK 8.0 or newer

## Running

1. Open the project in Godot (.NET build) and let it import.
2. Click the hammer (Build) at the top right, wait for "0 errors".
3. Open `Main.tscn` and press F6.

## Debug

If you don't see a hammer, you may need to go to Project -> Tools -> C# -> Create C# Solution.

## Controls

| Key | Action |
| --- | --- |
| Up / Down | Sim speed (0.25x to 1024x) |
| Tab | Toggle overview / follow-one-bot camera (follow mode drops to 1x) |
| Left / Right | Pick which bot to follow |
| S | Save the population locally (also autosaves every 5 generations) |
| E | Export the population into `saves/` so it can be committed to git |
| R | Restart from scratch (deletes the local save) |

## How training works

- Each bot is a small neural net (`Brain.cs`, 9 inputs, 16 hidden, 6 outputs) controlling
  thrust, turn, reach, shoot, power and loft.
- `Evolution.cs` keeps the top 4 bots unchanged and fills the rest with mutated copies of
  parents from the top quarter. Mutation strength rises slightly when progress stalls.
- The rim starts huge and shrinks toward regulation size once the population averages enough
  baskets (curriculum learning).
- Every bot in a generation sees the same ball and hoop positions, so scores are fair.

## Saves

Progress is stored in Godot's `user://` folder (outside the project). If there is no local save,
the sim loads `saves/population_stage3.bin` when present, so a trained population can travel
between machines through git.

## Files

- `Main.cs` – scene setup, training loop, HUD, camera, save/load
- `Humanoid.cs` – bot body, ball physics, rewards
- `Brain.cs` – feed-forward network
- `Evolution.cs` – selection and mutation
- `Main.tscn` – the scene (just a root node with `Main.cs` attached)
