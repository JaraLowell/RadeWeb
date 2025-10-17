# Animation Fix for Sitting/Standing Issue

## Problem
When an avatar sits on an object that plays animations (like furniture with pose animations), those animations would continue playing even after the avatar stands up. This was because the object animations were not being properly stopped when the sitting state changed.

## Solution
Based on the analysis of the Radegast codebase (https://github.com/cinderblocks/radegast), I implemented the same solution they use:

### Key Changes in `WebRadegastInstance.cs`:

1. **Added `StopAllAnimations()` method**: This method stops all non-system animations while preserving basic avatar animations like standing, walking, etc.

2. **Modified `SetSitting()` method**: When standing up (`sit = false`), the method now calls `StopAllAnimations()` to ensure object animations are stopped.

3. **Added `IsKnownSystemAnimation()` method**: This helper method identifies system animations that should not be stopped (standing, walking, flying, etc.).

## How It Works

1. When the user stands up from a seated position, `SetSitting(false)` is called
2. The method calls `Client.Self.Stand()` to send the stand command
3. It then calls `StopAllAnimations()` which:
   - Gets all currently active animations using `Client.Self.SignaledAnimations`
   - Filters out known system animations (standing, walking, etc.)
   - Stops all remaining animations (which includes object animations)
   - Sends the stop commands using `Client.Self.Animate()`

## Known System Animations
The following animations are preserved and not stopped:
- STAND, STAND_1, STAND_2, STAND_3, STAND_4
- WALK, RUN
- FLY, HOVER, HOVER_UP, HOVER_DOWN
- LAND, FALLDOWN
- SIT, SIT_GROUND, SIT_GROUND_CONSTRAINED, SIT_GENERIC
- SIT_TO_STAND, STAND_UP
- TURNLEFT, TURNRIGHT
- AWAY, BUSY, TYPE
- CROUCH, CROUCHWALK
- JUMP, PREJUMP, SOFT_LAND

## Usage
This fix is automatically applied whenever a user stands up. No additional code changes are needed in the UI or controllers - the functionality is built into the core `WebRadegastInstance` class.

## Testing
To test this fix:
1. Sit on an object that plays animations (like animated furniture)
2. Verify the animation starts playing
3. Stand up
4. Verify the animation stops immediately upon standing

## Compatibility
This fix follows the same approach used by the original Radegast client, ensuring compatibility with Second Life's animation system and expected user behavior.