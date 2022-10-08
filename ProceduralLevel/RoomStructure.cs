using System;
using UnityEngine;

[Serializable]
public class RoomStructure
{
    [Tooltip("The structure that will be placed in a room")]
    public GameObject ObjectToSpawn;
    
    [Tooltip("How much space will the structure occupy the room. 0 means the structure is infinitely small, and 100 means the structure will occupy much of the floor space of the room." +
             "Maximum is set to 75% to make space for spawn points.")]
    [Range(0f, 90f)]
    public float RadiusWidthPercentageOverride;
    
    [Tooltip("The minimum number of sides there should be in a room in order for this structure to spawn in that room")]
    public int MinSides;
    
    [Tooltip("The maximum number of sides there can be in a room in order for this structure to spawn in that room")]
    public int MaxSides;
    
    [Tooltip("(Not implemented yet)Should the structure be scaled uniformly in all three axes when stretching or shrinking to fit in a room?")]
    public bool ScaleUniformly = true;

    public bool IsValidRoom(PolygonRoom room) => room.edgeCount >= MinSides && room.edgeCount <= MaxSides;
}
