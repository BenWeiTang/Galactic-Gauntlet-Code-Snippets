using System.Linq;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
using Math = UnityEngine.ProBuilder.Math;
using Random = UnityEngine.Random;

public class ProceduralLevelManager : MonoBehaviour
{
    [Header("Room Specs")]
    [Tooltip("The largest possible number of edges a polygon can have")] [SerializeField, Min(3)]
    private int _maxEdgeCount = 6;

    [Tooltip("The length of each side of the polygon room")] [SerializeField]
    private float _length = 10f;

    [Tooltip("The height of the room")] [SerializeField]
    private float _height = 10f;

    [Header("Level Specs")] [Tooltip("How many rooms to generate")] [SerializeField]
    private int _roomCount = 5;

    [Tooltip("The horizontal spacing between two rooms")] [SerializeField]
    private float _roomSpacing = 1f;

    [Tooltip(
        "The maximum incline a connecting tunnel can have. Incline is rise over run. For example, an incline of 0.5 means that per 1 unit forward, there is a 0.5 unity increase in elevation.")]
    [SerializeField, Range(0f, 1f)]
    private float _inclineMax = 0.5f;

    [Header("Spawning")]
    [SerializeField] private BoxCollider _levelBounds;
    [SerializeField] private List<Transform> _spawnPoints;
    [SerializeField] private PlayerSpawner _playerSpawnerPrefab;

    [Header("Material")]
    [SerializeField] private Material _floorMaterial;
    [SerializeField] private Material _wallMaterial;
    [SerializeField] private Material _ceilingMaterial;
    [SerializeField] private Material _tunnelMaterial;

    [Header("Lighting")]
    [SerializeField] private bool _buildLights = true;
    [SerializeField] private GameObject _roomLight;
    [SerializeField] private float _lightRadiusMultiplier = 1.2f;
    [SerializeField] private float _wallLightDistance = 0.1f;

    [Header("In-room Structure")]
    [SerializeField] private List<RoomStructure> _roomStructures;
    [Range(0f, 100f), SerializeField] private float _emptyRoomPercentage;

    [Header("Debug")]
    [SerializeField] private bool _buildFloorOnly = false;
    [SerializeField] private bool debugBuildImmediately;

    private int _seed;

    // Constants reference
    private Dictionary<int, float> _inradii = new Dictionary<int, float>();
    private Dictionary<int, float> _circumradii = new Dictionary<int, float>();
    private Dictionary<int, float> _wholeSteps = new Dictionary<int, float>();
    private Dictionary<int, float> _halfSteps = new Dictionary<int, float>();
    private Dictionary<int, string> _polygonNames = new Dictionary<int, string>();

    private HashSet<ProBuilderMesh> _polygons = new HashSet<ProBuilderMesh>();
    private List<Transform> _tempTransforms = new List<Transform>();
    private HashSet<PolygonRoom> _rooms = new HashSet<PolygonRoom>();
    private HashSet<PolygonPort> _openPorts = new HashSet<PolygonPort>();
    private List<MeshRenderer> _meshRenderers = new List<MeshRenderer>();
    private System.Random _systemRandom = new System.Random();

#if UNITY_EDITOR
    [ContextMenu("Regenerate Rooms")]
    public void RegenerateRooms()
    {
        if (!UnityEditor.EditorApplication.isPlaying)
        {
            Debug.Log("Enter Play Mode before attempting to Regenerate Rooms.");
            return;
        }

        foreach (var polygon in _polygons)
            Destroy(polygon.gameObject);

        _polygons.Clear();
        _tempTransforms = new List<Transform>();
        _rooms.Clear();
        _openPorts.Clear();
        _meshRenderers.Clear();

        _systemRandom = new System.Random(_seed);
        Random.InitState(_seed);

        ConstantsInit();
        StartCoroutine(BuildRooms());
    }
#endif

    private void Awake()
    {
        GameMaster.OnMainInstanceSpawned((gm) => { CallbacksInit(); });
    }

    private void OnRoomGenerationFinished()
    {
        GameMaster.instance.RaiseEvent(CustomNetworkEvent.ProceduralLevelGenerationCompleted,
            Photon.Realtime.ReceiverGroup.Others, true);
    }

    private void CallbacksInit()
    {
        int playersInitialized = 0;

        GameMaster.instance.RegisterEvent<int>(CustomNetworkEvent.ProceduralSeed, BuildFromSeed);
        if (PhotonNetwork.IsMasterClient)
        {
            GameMaster.instance.RegisterEvent(CustomNetworkEvent.ProceduralLevelGenerationCompleted, () =>
            {
                Debug.Log("Player generated level.");
                playersInitialized++;
                if (playersInitialized >= PUNRoom.instance.playerInGame)
                {
                    GameMaster.instance.RaiseEvent(CustomNetworkEvent.AllPlayersProceduralLevelGenerated,
                        Photon.Realtime.ReceiverGroup.Others, true);
                }
            });

            int seed = Random.Range(0, 10_000);
            GameMaster.instance.RaiseEvent(CustomNetworkEvent.ProceduralSeed, Photon.Realtime.ReceiverGroup.Others, true,
                seed);
        }

        GameMaster.instance.RegisterEvent(CustomNetworkEvent.AllPlayersProceduralLevelGenerated,
            () => { Instantiate(_playerSpawnerPrefab); });
    }

    private void Update()
    {
        if (debugBuildImmediately) //mostly just used for debugging
        {
            BuildFromSeed(Random.Range(int.MinValue, int.MaxValue));
            debugBuildImmediately = false;
        }
    }

    private void BuildFromSeed(int seed)
    {
        // Start creating rooms with seed
        _systemRandom = new System.Random(seed);
        Random.InitState(seed);
        ConstantsInit();
        StartCoroutine(BuildRooms());
    }

    private void ConstantsInit()
    {
        for (int i = 3; i <= _maxEdgeCount; i++)
        {
            float circumradius = 0.5f * _length * (1 / Mathf.Sin(Mathf.PI / i));
            _circumradii[i] = circumradius;

            float inradius = 0.5f * _length * (1 / Mathf.Tan(Mathf.PI / i));
            _inradii[i] = inradius;

            float wholeStep = 2 * Mathf.PI / i;
            _wholeSteps[i] = wholeStep;

            float halfStep = Mathf.PI / i;
            _halfSteps[i] = halfStep;

            switch (i)
            {
                case 3:
                    _polygonNames[i] = "Triangle";
                    break;
                case 4:
                    _polygonNames[i] = "Square";
                    break;
                case 5:
                    _polygonNames[i] = "Pentagon";
                    break;
                case 6:
                    _polygonNames[i] = "Hexagon";
                    break;
                case 7:
                    _polygonNames[i] = "Heptagon";
                    break;
                case 8:
                    _polygonNames[i] = "Octagon";
                    break;
                case 9:
                    _polygonNames[i] = "Nonagon";
                    break;
                case 10:
                    _polygonNames[i] = "Decagon";
                    break;
                default:
                    _polygonNames[i] = $"{i}-sided polygon";
                    break;
            }
        }
    }

    private IEnumerator BuildRooms()
    {
        yield return FindSpace();
#if UNITY_EDITOR
        if (_buildFloorOnly)
        {
            foreach (var room in _rooms)
            {
                BuildFloorMesh(room);
            }

            OnRoomGenerationFinished();
            yield break;
        }
#endif
        foreach (var room in _rooms)
        {
            BuildFloorMesh(room);
            BuildWallAndCeilingMesh(room);
        }

        ConnectRooms();
        BuildBounds();
        BuildStructures();
        BuildLights(); // <- needs to execute after BuildStructures
        UpdateSpawnPoints(); // <- needs to execute after BuildStructures
        OnRoomGenerationFinished();
    }

    //FIXME: time is O(n^2) or even worse
    // Might want to prioritize trying ports that are farthest away from the center
    private IEnumerator FindSpace()
    {
        for (int i = 0; i < _roomCount; i++)
        {
            // First polygon
            if (_rooms.Count == 0)
            {
                var tt = new GameObject($"Temp {i}").transform;
                _tempTransforms.Add(tt);
                tt.SetParent(transform);

                int rnd = GetRandomEdgeCount();
                var vertices = GetVertices(tt, rnd);
                var inradius = _inradii[rnd];
                var circumradius = _circumradii[rnd];
                var portsPositions = GetPortsPositions(tt, rnd);

                List<PolygonPort> ports = new List<PolygonPort>();
                foreach (var pos in portsPositions)
                    ports.Add(new PolygonPort(pos));

                var room = new PolygonRoom(tt.position, vertices, ports, inradius, circumradius, rnd);
                _rooms.Add(room);

                foreach (var port in ports)
                    _openPorts.Add(port);

                yield return null;
            }
            // The rest of the polygons
            else
            {
                // Loop until a valid spot is found
                while (true)
                {
                    PolygonPort rndPort = GetRandomPort();

                    var currentRoom = rndPort.NativeRoom;
                    Vector3 currentCenter = currentRoom.floorCenter;

                    int newEdgeCount = GetRandomEdgeCount();
                    float newInradius = _inradii[newEdgeCount];
                    float newCircumradius = _circumradii[newEdgeCount];
                    Vector3 direction = (rndPort.Position - currentCenter).normalized;
                    Vector3 newCenter = rndPort.Position + direction * (newInradius + _roomSpacing);

                    // For all the polygons except the current one that the new polygon is branching out from,
                    // if none intersects with this new polygon, this polygon at this port will fit 
                    bool isValidSpot = _rooms.Where(room => room != currentRoom)
                        .All(room => room.Intersects(newCenter, newCircumradius) == false);

                    if (!isValidSpot)
                    {
                        // GetRandomPort() removes an entry from _openPorts
                        // So, add back when found no fit in current iteration
                        _openPorts.Add(rndPort);
                        yield return null;
                    }
                    else
                    {
                        // Found a valid spot, so construct a temporary Transform whose forward direction
                        // points at the old room
                        var tt = new GameObject($"Temp {i}").transform;
                        _tempTransforms.Add(tt);
                        tt.position = newCenter;
                        tt.SetParent(transform);
                        tt.LookAt(currentCenter);
                        tt.position += Vector3.up * GetRandomHeightDelta();

                        // With the right orientation of tt, GetVertices() and GetPorts() should return
                        // the right Vector3's
                        var vertices = GetVertices(tt, newEdgeCount);
                        var inradius = _inradii[newEdgeCount];
                        var circumradius = _circumradii[newEdgeCount];

                        var portsPositions = GetPortsPositions(tt, newEdgeCount);
                        List<PolygonPort> ports = new List<PolygonPort>();
                        foreach (var pos in portsPositions)
                            ports.Add(new PolygonPort(pos));

                        var room = new PolygonRoom(tt.position, vertices, ports, inradius, circumradius, newEdgeCount);
                        _rooms.Add(room);

                        // Of all the new ports, the closet one to the rndPort is the one that is
                        // used to connect to the original PolygonRoom.

                        // Therefore, by the time the room is created, it should automatically 
                        // be considered as a port in use

                        // Also, when execution enters this else-block, GetRandomPort() has been called
                        // once. Therefore, only this portToClose needs to be removed from _openPorts
                        // because rndPort has already been removed
                        PolygonPort portToClose = ports.Aggregate((min, next) =>
                            Vector3.Distance(min.Position, rndPort.Position) <
                            Vector3.Distance(next.Position, rndPort.Position)
                                ? min
                                : next);
                        foreach (var port in ports)
                            _openPorts.Add(port);

                        _openPorts.Remove(portToClose);

                        PairPorts(portToClose, rndPort);

                        // Break out this while loop since a polygon has been built
                        break;
                    }
                }
            }
        }

        foreach (var tt in _tempTransforms)
            Destroy(tt.gameObject);

        _tempTransforms.Clear();
        _tempTransforms = null;
    }

    private void BuildFloorMesh(PolygonRoom room)
    {
        List<Face> faces = new List<Face>();
        for (int i = 0; i < room.edgeCount; i++)
        {
            int j = i + 1;
            if (j == room.edgeCount)
            {
                j = 0;
            }

            // An n-sided regular polygons are made of n identical isosceles triangles, laid out leg to leg.
            // To visualize:
            // Label the center vertex as 0, and go around labeling each vertex of the polygon clock-wise.
            // Starting from the center (at index 0) and going around each of those identical isosceles 
            // triangles' vertices-for each of these triangles we go through their vertices clock-wise as well-
            // we will be able get the ordered list of indices necessary for drawing out the n triangles as faces
            var face = new Face(new int[] {0, i + 1, j + 1});
            faces.Add(face);
        }

        var polygon = ProBuilderMesh.Create(room.floorVertices, faces);
        var mergedFace = MergeElements.Merge(polygon, faces); // Merge all triangles into a single face

        polygon.transform.SetParent(transform);
        polygon.name = _polygonNames[room.edgeCount];
        _polygons.Add(polygon);
        room.SetMesh(polygon);

        var meshRenderer = polygon.GetComponent<MeshRenderer>();
        meshRenderer.sharedMaterials = new Material[4]
        {
            _floorMaterial,
            _wallMaterial,
            _ceilingMaterial,
            _tunnelMaterial
        };
        _meshRenderers.Add(meshRenderer);
        mergedFace.submeshIndex = 0; // Use floor material
    }

    private void BuildWallAndCeilingMesh(PolygonRoom room)
    {
        // Source: https://github.com/Unity-Technologies/ProBuilder-API-Examples/blob/master/Runtime/Procedural%20Mesh/ExtrudeRandomEdges.cs
        // Still a little confused about the we => we.edge.local, but basically we want the actual marginal sides
        // of the polygon, not the internal edges that make up the internal triangles 

        // Build the walls first
        IEnumerable<Edge> nonManifoldEdges = WingedEdge.GetWingedEdges(room.Mesh)
            .Where(we => we.opposite == null) // Get open edges
            .Select(we => we.edge.local);
        Edge[] extrudedEdges = room.Mesh.Extrude(nonManifoldEdges, 0f, true, true);
        room.Mesh.TranslateVertices(extrudedEdges, Vector3.up * _height);

        // Apply wall material to walls 
        IEnumerable<Face> newWalls = WingedEdge.GetWingedEdges(room.Mesh)
            .Where(we => we.opposite == null)
            .Select(we => we.face);
        foreach (var wall in newWalls) wall.submeshIndex = 1;

        // Build the ceiling: extrude again and merge all vertices of the newly extruded edges
        Edge[] ceilingTempEdges = room.Mesh.Extrude(extrudedEdges, 0f, true, true);
        int[] ceilingVertices = ceilingTempEdges
            .Select(edge => edge.a)
            .ToArray();
        room.Mesh.MergeVertices(ceilingVertices);

        // Apply ceiling material to ceiling; first find the ceiling face
        foreach (var face in room.Mesh.faces)
        {
            var normal = Math.Normal(room.Mesh, face);
            if (Vector3.Dot(normal.normalized, Vector3.down) > 0.99f)
                face.submeshIndex = 2;
        }

        room.Mesh.ToMesh(); //Rebuild the mesh
        room.Mesh.Refresh(); // Recalculate UI, normal, and stuff
    }

    private void ConnectRooms()
    {
        foreach (var port in _rooms.SelectMany(r => r.ports).Where(p => p.HasConnection))
        {
            port.OpenPort(); // This is kind of expensive
        }
    }

    private void BuildBounds()
    {
        Bounds bounds = _levelBounds.bounds;
        foreach (var meshRenderer in _meshRenderers)
        {
            bounds.Encapsulate(meshRenderer.bounds);
        }

        bounds.Expand(20f);

        // For some reason, we need to do this
        _levelBounds.center = bounds.center;
        _levelBounds.size = bounds.size;

        foreach (var meshCollider in _polygons.Select(polygon => polygon.GetOrAddComponent<MeshCollider>()))
        {
            meshCollider.convex = false;
        }
    }

    private void BuildStructures()
    {
        foreach (var room in _rooms)
        {
            if (Random.Range(0f, 100f) < _emptyRoomPercentage)
            {
                continue;
            }

            var type = _roomStructures
                .Where(rs => rs.IsValidRoom(room))
                .OrderBy(x => _systemRandom.Next())
                .FirstOrDefault();

            if (type == null)
            {
                continue;
            }

            var instance = Instantiate(type.ObjectToSpawn, room.floorCenter, quaternion.identity);
            var roomTransform = room.Mesh.transform;
            var structureTransform = instance.transform;
            structureTransform.forward = roomTransform.forward; // Can change to something else
            structureTransform.SetParent(roomTransform);

            Bounds bounds = new Bounds();
            bounds.center = structureTransform.position;
            foreach (var meshRenderer in instance.GetComponentsInChildren<MeshRenderer>())
            {
                bounds.Encapsulate(meshRenderer.bounds);
            }

            var longest = Mathf.Max(bounds.size.x, bounds.size.z);
            var targetLength = type.RadiusWidthPercentageOverride * room.inradius * 0.01f;
            var scale = targetLength / longest;
            structureTransform.localScale = Vector3.one * scale;
            room.SetStructure(type, instance);
        }
    }

    private void BuildLights()
    {
        if (!_buildLights)
        {
            var allStructureLights = FindObjectsOfType<RoomLightController>();
            foreach (var roomLightController in allStructureLights)
            {
                roomLightController.SetLightActive(false);
            }
            return;
        }
        
        float hueStartValue = (float) _systemRandom.NextDouble(); //random hues, equidistant from each other
        int hueOffsetIndex = 0;
        foreach (var room in _rooms)
        {
            SpawnWallLightsInRoom(room);
            room.BuildLights();
            foreach (var lightController in room.LightControllers)
            {
                lightController.SetColor(hueStartValue + (1f / _roomCount * hueOffsetIndex) % 1);
                lightController.SetRange(room.circumradius * _lightRadiusMultiplier);
                lightController.SetLightActive(true);
            }

            hueOffsetIndex++;
        }

        void SpawnWallLightsInRoom(PolygonRoom room)
        {
            Vector3 heightCorrectedCenter = room.floorCenter + Vector3.up * _height * 0.5f;

            foreach (var position in GetWallLightsPositions(room))
            {
                var go = Instantiate(_roomLight, position, Quaternion.identity);
                go.name = "Wall Light";
                go.transform.LookAt(heightCorrectedCenter);
                go.transform.SetParent(room.Mesh.transform);
            }
        }

        IEnumerable<Vector3> GetWallLightsPositions(PolygonRoom room)
        {
            // room.floorVertices include the center vertex which is at index 0
            // so we start with index 1 and when wrapping for next, we wrap to 1 not 0
            for (int i = 1; i < room.floorVertices.Count(); i++)
            {
                var current = room.floorVertices.ElementAt(i);
                int nextIndex = i + 1;
                nextIndex = nextIndex == room.floorVertices.Count() ? 1 : nextIndex; // when nextIndex is out of bound, go back to 1
                var next = room.floorVertices.ElementAt(nextIndex);

                var newLightPosition = (current + next) * 0.5f; // Light position is in between two floor vertices

                // At this point, the light position is still on ground level,
                // which is the same height as Position of a PolygonPort of every PolygonRoom
                // Check if newLightPosition is in the same position (or super close) to any port's position
                // If so, there should not be a wall light so continue the foreach loop
                // Also, not all ports of a room have connections; some are closed ports, and others are open
                // So check against HasConnections to filter out closed ports
                var openPortsPositions = room.ports.Where(port => port.HasConnection).Select(port => port.Position);
                if (openPortsPositions.Any(pos => Vector3.Distance(pos, newLightPosition) <= _wallLightDistance))
                    continue;

                newLightPosition += (room.floorCenter - newLightPosition) * 0.01f; // Move light position towards center a bit; avoid z fighting
                newLightPosition += Vector3.up * _height * 0.5f; // Move light position up
                yield return newLightPosition;
            }
        }
    }

    private void UpdateSpawnPoints()
    {
        var centers = _rooms.Select(room => room.floorCenter);
        if (_spawnPoints.Count() > centers.Count())
        {
            Debug.LogWarning(
                $"Not enough rooms to support the number of spawn points in this level.\nCurrent spawn points: {_spawnPoints.Count()}\nCurrent rooms: {_rooms.Count()}");
        }
        else
        {
            var spawnRooms = _rooms.OrderBy(x => _systemRandom.Next()).Take(_spawnPoints.Count()).ToArray();
            for (int i = 0; i < _spawnPoints.Count(); i++)
            {
                var currentRoom = spawnRooms[i];
                Vector3 targetPosition = currentRoom.floorCenter + Vector3.up * 1.5f;
                if (currentRoom.Structure != null)
                {
                    targetPosition += GetRandomOnUnitCircle() *
                                      (currentRoom.StructureType.RadiusWidthPercentageOverride + 5f) *
                                      currentRoom.inradius * 0.01f;
                }
                else
                {
                    var tempRandom = Random.insideUnitCircle;
                    targetPosition += new Vector3(tempRandom.x, 0f, tempRandom.y) * 0.5f * currentRoom.inradius;
                }

                _spawnPoints[i].position = targetPosition;
            }
        }
    }


    /// <summary>
    /// The parameter location has to be a transform that has already been oriented the towards the right direction.
    /// Note that the center of the polygon is also a vertex at index 0 of the return IEnumerable
    ///</summary>
    private IEnumerable<Vector3> GetVertices(Transform location, int edgeCount)
    {
        float halfStep = _halfSteps[edgeCount];
        float wholeStep = _wholeSteps[edgeCount];
        float circumradius = _circumradii[edgeCount];

        yield return location.position;

        for (int i = 0; i < edgeCount; i++)
        {
            float angle = halfStep + i * wholeStep;
            float x = Mathf.Sin(angle) * circumradius;
            float z = Mathf.Cos(angle) * circumradius;

            // x and z are local to polygon, so transform then to world space
            yield return location.TransformPoint(new Vector3(x, 0f, z));
        }
    }

    /// <summary>
    /// The parameter location has to be a transform that has already been oriented the towards the right direction.
    ///</summary>
    private IEnumerable<Vector3> GetPortsPositions(Transform location, int edgeCount)
    {
        float wholeStep = _wholeSteps[edgeCount];
        float inradius = _inradii[edgeCount];

        for (int i = 0; i < edgeCount; i++)
        {
            float angle = wholeStep * i;
            float x = Mathf.Sin(angle) * inradius;
            float z = Mathf.Cos(angle) * inradius;

            // x and z are local to polygon, so transform then to world space
            yield return location.TransformPoint(new Vector3(x, 0f, z));
        }
    }

    private int GetRandomEdgeCount() => Random.Range(3, _maxEdgeCount + 1);

    private PolygonPort GetRandomPort()
    {
        var result = _openPorts.OrderBy(p => _systemRandom.Next()).First();
        _openPorts.Remove(result);
        return result;
    }

    private float GetRandomHeightDelta()
    {
        float absDelta = _inclineMax * _roomSpacing;
        return Random.Range(-absDelta, absDelta);
    }

    ///<summary>
    /// Connect two PolygonPorts and update the SisterPort and ConnectedRoom of each PolygonPort
    ///</summary>
    private static void PairPorts(PolygonPort a, PolygonPort b)
    {
        if (a == b)
        {
            Debug.LogError("Cannot connect a port to itself.");
            return;
        }

        a.SetSisterPort(b);
        a.SetConnectedRoom(b.NativeRoom);
        b.SetSisterPort(a);
        b.SetConnectedRoom(a.NativeRoom);
    }

    private Vector3 GetRandomOnUnitCircle()
    {
        var x = Random.Range(0f, 1f);
        var z = Mathf.Sqrt(1 - Mathf.Pow(x, 2));
        return new Vector3(x, 0f, z);
    }
}
