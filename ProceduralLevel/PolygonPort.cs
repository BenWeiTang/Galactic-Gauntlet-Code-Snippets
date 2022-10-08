using System.Collections.Generic;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

public class PolygonPort
{
    ///<summary>
    /// The position of this port. The Y component is at the same level as the port's floor, which
    /// might not be the true representation of the port's veritcal disposition
    ///</summary>
    public Vector3 Position { get; private set; }
    public PolygonRoom NativeRoom { get; private set; } = null;
    public PolygonRoom ConnectedRoom { get; private set; } = null;
    public PolygonPort SisterPort { get; private set; } = null;
    public bool HasConnection => SisterPort != null;
    public IEnumerable<Edge> Edges { get; private set; }
    public bool HasBuiltTunnel { get; set; } = false;

    public PolygonPort(Vector3 position, PolygonRoom nativeRoom = null,
        PolygonRoom connectedRoom = null, PolygonPort sisterPort = null)
    {
        Position = position;
        NativeRoom = nativeRoom;
        ConnectedRoom = connectedRoom;
        SisterPort = sisterPort;
    }

    public void SetSisterPort(PolygonPort other) => SisterPort = other;
    public void SetNativeRoom(PolygonRoom room) => NativeRoom = room;
    public void SetConnectedRoom(PolygonRoom room) => ConnectedRoom = room;
    public void OpenPort()
    {
        ProBuilderMesh mesh = NativeRoom.Mesh;
        Vector3 portToCenter = NativeRoom.floorCenter - Position;
        Vector3 portToCenterLocal = mesh.transform.InverseTransformDirection(portToCenter);

        IEnumerable<Face> faces = mesh.faces;
        foreach (Face face in faces)
        {
            var normal = Math.Normal(mesh, face);

            // Using dot product to find the face with which this port is supposed to extrude a tunnel
            // normal is in local (model) space, so translate portToCenter to local first in order to
            // perform dot product.
            // Now, if the current normal is in the same direction as portToCenterLocal,
            // that means the current face is the Face that we want,
            // and, in that case, their dot product should be 1; using 0.99f to be safe
            if (Vector3.Dot(normal.normalized, portToCenterLocal.normalized) >= 0.99f)
            {
                // In case where _roomSpacing is 0f or very small, don't extrude
                if (Vector3.Distance(SisterPort.Position, Position) < 0.02f) return;

                Face[] tunnelWalls = mesh.Extrude(new Face[] { face }, ExtrudeMethod.IndividualFaces, 0f);
                foreach (var wall in tunnelWalls) wall.submeshIndex = 3;

                // Extrude half way towards this port's sister port
                Vector3 direction = 0.5f * (SisterPort.Position - Position);
                mesh.TranslateVertices(new Face[] { face }, direction);

                mesh.DeleteFace(face);
                mesh.ToMesh();
                mesh.Refresh();
                return;
            }
        }
    }
}
