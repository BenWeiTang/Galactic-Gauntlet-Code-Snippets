using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.ProBuilder;

public class PolygonRoom
{
    /// <summary>
    /// The center of the room on the ground level. Note that this is not the actual center point of the room.
    /// </summary>
    public readonly Vector3 floorCenter;
    /// <summary>
    /// The positions of vertices on the ground level. There are n + 1 floor vertices, where n equals the Edge Count of the room. Note that vertex at index 0 is the same as Floor Center. Real corners' indices start at index 1.
    /// </summary>
    public readonly IEnumerable<Vector3> floorVertices;
    /// <summary>
    /// The PolygonPort's of a room. Every room has n ports, where n equals the Edge Count of the room. Note that some ports might be closed despite being a port, meaning that they have no connections to any other port.
    /// </summary>
    public readonly IEnumerable<PolygonPort> ports;
    public readonly float inradius;
    public readonly float circumradius;
    /// <summary>
    /// The number of walls there are in a PolygonRoom.
    /// </summary>
    public readonly int edgeCount;
    public ProBuilderMesh Mesh {get; private set;}
    public RoomStructure StructureType { get; private set; }
    public GameObject Structure { get; private set; }
    public IEnumerable<RoomLightController> LightControllers { get; private set; }


    ///<summary>A neighbor is another PolygonRoom that connects to this PolygonRoom</summary>
    public IEnumerable<PolygonRoom> Neighbors => ports.Where(p => p.HasConnection).Select(p => p.ConnectedRoom);

    ///<summary>A PolygonRoom is a leaf when it has only one connection</summary>
    public bool IsALeaf => ports.Count(p => p.HasConnection) == 1;

    public PolygonRoom(Vector3 floorCenter, IEnumerable<Vector3> vertices,
        IEnumerable<PolygonPort> ports, float inradius, float circumradius, int edgeCount)
    {
        this.floorCenter = floorCenter;
        this.floorVertices = vertices;
        this.ports = ports;
        this.inradius = inradius;
        this.circumradius = circumradius;
        this.edgeCount = edgeCount;

        foreach(var port in this.ports)
            port.SetNativeRoom(this);
    }

    ///<summary>Returns true if the PolygonRoom would-be intersects with this PolygonRoom</summary>
    public bool Intersects(Vector3 newCenter, float newCircumradius)
    {
        float distance = Vector3.Distance(this.floorCenter, newCenter);
        float radiiSum = this.circumradius + newCircumradius;
        return distance < radiiSum;
    }

    public void SetMesh(ProBuilderMesh mesh) => Mesh = mesh;

    public void SetStructure(RoomStructure type, GameObject instance)
    {
        StructureType = type;
        Structure = instance;
    }
    
    public void BuildLights()
    {
        LightControllers = Mesh.transform.GetComponentsInChildren<RoomLightController>();
    }
}
