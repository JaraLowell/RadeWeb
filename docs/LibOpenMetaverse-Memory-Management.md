# LibOpenMetaverse Memory Management

## Overview

The RadegastWeb application uses the LibOpenMetaverse library for Second Life protocol implementation. This library maintains various internal collections that can grow over time, potentially causing memory leaks in long-running applications.

## Implemented Solutions

### 1. Automatic Periodic Cleanup

The `RadegastBackgroundService` now includes automatic LibOpenMetaverse cleanup every 30 minutes:

```csharp
// Periodic LibOpenMetaverse cleanup (every 30 minutes) to prevent memory leaks
if (now - lastLibOpenMetaverseCleanup >= TimeSpan.FromMinutes(30))
{
    await PerformLibOpenMetaverseCleanupAsync(stoppingToken);
    lastLibOpenMetaverseCleanup = now;
}
```

### 2. WebRadegastInstance Cleanup Method

Each `WebRadegastInstance` has a `PerformLibOpenMetaverseCleanup()` method that:

- **Avatar Cleanup**: Removes avatar objects beyond 512m range from ObjectsAvatars collection
- **Primitive Cleanup**: Removes primitive objects beyond 256m draw distance from ObjectsPrimitives collection  
- **Coarse Location Cleanup**: Removes distant avatar positions from AvatarPositions collection (>1024m)
- **Asset Cache Maintenance**: Temporarily enables and triggers asset cache pruning

### 3. Distance-Based Cleanup Strategy

Since LibOpenMetaverse objects don't have reliable timestamp properties, the cleanup uses distance-based filtering:

```csharp
// Clean up distant avatars (beyond sim range)
var avatarsToRemove = currentSim.ObjectsAvatars.Values
    .Where(a => a.ID != _client.Self.AgentID)
    .Where(a => Calculate3DDistance(ourPosition, GetAvatarActualPosition(a)) > 512.0f)
    .Select(a => a.LocalID)
    .Take(50) // Limit to prevent excessive removal
    .ToList();
```

### 4. Manual Cleanup API

A new endpoint allows manual triggering of LibOpenMetaverse cleanup:

**POST** `/api/memory/cleanup-libopenmv`

Returns cleanup results including:
- Number of accounts cleaned
- Memory usage before/after
- Memory freed amount

## Collections Cleaned

### Per Simulator Collections
- **ObjectsAvatars**: Avatar objects keyed by LocalID
- **ObjectsPrimitives**: Primitive objects keyed by LocalID  
- **AvatarPositions**: Coarse avatar positions keyed by UUID
- **Terrain**: Terrain patch data (conditional cleanup)

### Asset Cache
- **Assets.Cache**: Texture and asset cache with temporary pruning enabled

## Safety Measures

1. **Distance Limits**: Only removes objects beyond reasonable interaction range
2. **Quantity Limits**: Limits removal count per cleanup cycle (50 avatars, 100 prims, 25 positions)
3. **Self-Exclusion**: Never removes data for the account's own avatar
4. **Connection Checks**: Only performs cleanup on connected accounts
5. **Exception Handling**: All cleanup wrapped in try-catch blocks

## Monitoring

### Memory Controller Endpoints

- **GET** `/api/memory/stats` - Comprehensive memory statistics
- **POST** `/api/memory/gc` - Force garbage collection 
- **POST** `/api/memory/cleanup-libopenmv` - Manual LibOpenMetaverse cleanup
- **GET** `/api/memory/per-account` - Per-account memory usage analysis

### Logging

Cleanup operations are logged with details:

```
LibOpenMetaverse cleanup for account {AccountId}: cleaned {CleanedItems} items, saved {MemoryMB:F1}MB memory
```

## Expected Benefits

1. **Reduced Memory Growth**: Prevents unbounded collection growth in busy regions
2. **Improved Performance**: Smaller collections mean faster lookups and iterations
3. **Stability**: Reduces risk of out-of-memory errors in long-running instances
4. **Automatic Maintenance**: No manual intervention required for normal operation

## Configuration

The cleanup intervals can be adjusted in `RadegastBackgroundService.cs`:

```csharp
// Change cleanup frequency (default: 30 minutes)
if (now - lastLibOpenMetaverseCleanup >= TimeSpan.FromMinutes(30))

// Adjust distance thresholds in WebRadegastInstance.cs:
.Where(a => Calculate3DDistance(ourPosition, GetAvatarActualPosition(a)) > 512.0f) // Avatar range
.Where(p => Calculate3DDistance(ourPosition, p.Position) > 256.0f) // Primitive range
.Where(kvp => Calculate3DDistance(ourPosition, kvp.Value) > 1024.0f) // Coarse position range
```

## Testing

Use the manual cleanup endpoint to test effectiveness:

```bash
curl -X POST http://localhost:5000/api/memory/cleanup-libopenmv
```

Monitor memory usage over time to verify cleanup is working:

```bash
curl http://localhost:5000/api/memory/stats
```

This implementation should significantly reduce the memory growth issues caused by LibOpenMetaverse internal collections while maintaining full functionality for nearby objects and avatars.