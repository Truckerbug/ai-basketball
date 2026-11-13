# Basketball Bots

AI Warehouse-style neuroevolution in Godot 4 (C#). Forty humanoid bots play twenty half-court
1v1 games at a time and evolve to shoot, defend, steal and rebound. No physics engine is involved
in training: bot movement and ball physics (gravity, bounces, backboard, rim iron) are simulated
by a small custom loop so generations run hundreds of times faster than real time.

## Requirements

- Godot 4.7.2 **.NET** build (not the standard build)
- .NET SDK 8.0 or newer

## Running

1. Open the project in Godot (.NET build) and let it import.
2. Click the hammer (Build) at the top right, wait for "0 errors".
3. Open `Main.tscn` and press F6.

## Controls

| Key | Action |
| --- | --- |
| Up / Down | Sim speed (0.25x to 1024x) |
| Tab | Toggle overview / watch-one-game camera (watch mode drops to 1x) |
| Left / Right | Pick which game to watch |
| S | Save the population locally (also autosaves every 5 generations) |
| E | Export the population into `saves/` so it can be committed to git |
| R | Restart from scratch (deletes the local save) |

## The game

- One ball, one hoop. The bot holding the ball is on offense, the other plays defense.
- Pick up a loose ball by standing close, facing it, with arms out.
- Steal: stay close to the ball carrier, facing the ball, arms out, for about a third of a second.
- 8 second shot clock. Hold the ball too long and you lose it.
- After a basket the bot that conceded gets the ball and both bots reset.
- A basket counts only when the ball drops down through the rim without touching the iron,
  and it did not come up through the hoop from below.
- Blue and red are just the two slots in a game; pairings are reshuffled every generation.
  A gold dot floats over whoever has the ball.

## How training works

- Each bot is a small neural net (`Brain.cs`, 13 inputs, 20 hidden, 6 outputs) controlling
  thrust, turn, reach, shoot, power and loft.
- `Evolution.cs` keeps the top 4 bots unchanged and fills the rest with mutated copies of
  parents from the top quarter. Mutation strength rises slightly when progress stalls.
- Fitness: +100 per basket, -30 for conceding one, steals are zero-sum (+10 for the stealer, -10 for
  the victim), up to +15 for near-miss arcs (only for shots that travel at least 3 m),
  small penalties for shot clock violations and for waving arms around for no reason.
- The rim starts a little large and shrinks toward regulation size once the population averages
  enough baskets (curriculum learning).
- On first run the stage 3 solo-shooter brains (`population_stage3c.bin` or `population_stage3b.bin`)
  are converted into the bigger network and used as a warm start.

## Saves

Progress is stored in Godot's `user://` folder (outside the project). If there is no local save,
the sim loads `saves/population_stage4c.bin` when present, so a trained population can travel
between machines through git.

## Files

- `Main.cs` - scene setup, pairing, training loop, HUD, camera, save/load, warm-start conversion
- `Match.cs` - the 1v1 rules, shared ball physics, steals, scoring and rewards
- `Humanoid.cs` - bot body, movement and visuals
- `Brain.cs` - feed-forward network
- `Evolution.cs` - selection and mutation
- `Main.tscn` - the scene (just a root node with `Main.cs` attached)
