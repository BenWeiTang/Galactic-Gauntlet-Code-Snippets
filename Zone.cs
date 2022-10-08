using Photon.Pun;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[RequireComponent(typeof(PhotonView))]
public class Zone : APlayerTriggerArea, INetworkComponent, IHudMarkerPositioner
{
    [Header("HUD Appearances")]
    [SerializeField] MeshRenderer zoneRenderer;
    public Sprite hudMarkerNeutralSprite;
    public Sprite hudMarkerFriendlySprite;
    public Sprite hudMarkerEnemySprite;
    public Sprite progressCircleSprite;
    public Sprite progressCircleBackground;

    [Header("Zone Appearances")]
    [Range(0f, 1f)] private float _fieldAlpha = 0.3f;
    [Tooltip("The rate at which the color of the zone blinks when being captured. (Avoid 0)")]
    [SerializeField, Min(0f)] private float _zoneBlinkingSpeed = 1f;

    [Header("Zone Behavior")]
    [Tooltip("A Super Zone does not listen to Zone Manager's activation/deacitvation call. A Super Zone also does not count towards the number of active zones.")]
    public bool superZone;

    public float CaptureMeter
    {
        get
        {
            if (this.IsSending())
            {
                if (State == ZoneState.Neutral)
                {
                    if (CurrentAdvantagedTeam != null)
                    {
                        return CurrentAdvantagedTeam.PercentageInCurrentZone;
                    }
                    else
                    {
                        return 0f;
                    }
                }
                else if (State == ZoneState.Captured)
                {
                    return CurrentOccupyingTeam.PercentageInCurrentZone;
                }
                else
                {
                    return 0f;
                }
            }
            else
            {
                if (State == ZoneState.Neutral)
                {
                    if (_networkAdvantagedTeamNumber != int.MaxValue)
                    {
                        return _networkAdvantagedTeamProgress;
                    }
                    else
                    {
                        return 0f;
                    }
                }
                else if (State == ZoneState.Captured)
                {
                    return _networkOccupyingTeamProgress;
                }
                else
                {
                    return 0f;
                }
            }
        }
    }

    /// <summary>
    /// The current state of this zone. Note that State is also synced across the network, so client side can also use it despite not having the _network prefix.
    /// </summary>
    public ZoneState State { get; protected set; } = ZoneState.Neutral;

    public bool IsContested => _inZoneTeams.Count > 1;
    public ZoneTeam CurrentOccupyingTeam { get; protected set; } = null;
    public ZoneTeam CurrentAdvantagedTeam { get; protected set; } = null;

    private bool _isReadyToUpdate
    {
        get { return _isGameMasterReady && _isUserPlayerReady && _isHUDReady; }
    }

    private bool _isGameMasterReady = false;
    private bool _isUserPlayerReady = false;
    private bool _isHUDReady = false;
    private HashSet<APhysicsPlayer> _inZonePlayers = new HashSet<APhysicsPlayer>();
    private Dictionary<int, ZoneTeam> _inZoneTeams = new Dictionary<int, ZoneTeam>();
    private float _nextScoreUpdateTime;

    // Unity component caches
    private HUDMarker _stateHudMarker;
    private Image _stateHudMarkerImage;
    private Image _progressHudMarkerImage;
    private Image _progressBackgroundImage;
    private TextMeshProUGUI _stateText;
    private Renderer[] _zoneRenderers;

    // Param caches
    private ZoneControlData _zoneControlData;
    private int _myTeamNumber;
    private float _scoreUpdatePeriod;
    private float _capturedZoneCaptureRate;
    private float _neutralZoneCaptureRate;

    // Network related
    private int _networkAdvantagedTeamNumber = int.MaxValue;
    private int _networkOccupyingTeamNumber = int.MaxValue;
    private float _networkAdvantagedTeamProgress;
    private float _networkOccupyingTeamProgress;
    private bool _networkIsContested = false;
    private bool _networkIsCapturing = false;
    private bool _networkIsNeutralizing = false;

    #region INetworkComponent

    public bool networkComponentInitialized { get; set; }

    public PhotonView photonView { get; set; }

    public SyncDirection syncDirection => PhotonNetwork.IsMasterClient ? SyncDirection.Send : SyncDirection.Receive;

    public bool syncOnInit => true;

    public float? syncRate => null;

    private object[] toSend;

    public object[] Send(bool forceSync)
    {
        if (toSend == null)
        {
            toSend = new object[9];
        }

        int i = 0;
        toSend[i++] = zoneRenderer.gameObject.activeSelf;
        toSend[i++] = State;

        _networkOccupyingTeamNumber = CurrentOccupyingTeam != null ? CurrentOccupyingTeam.TeamNumber : int.MaxValue;
        _networkOccupyingTeamProgress = CurrentOccupyingTeam != null ? CurrentOccupyingTeam.PercentageInCurrentZone : 0f;
        _networkAdvantagedTeamNumber = CurrentAdvantagedTeam != null ? CurrentAdvantagedTeam.TeamNumber : int.MaxValue;
        _networkAdvantagedTeamProgress = CurrentAdvantagedTeam != null ? CurrentAdvantagedTeam.PercentageInCurrentZone : 0f;
        _networkIsContested = IsContested;
        _networkIsCapturing = State == ZoneState.Neutral && _inZoneTeams.Count == 1;
        _networkIsNeutralizing = State == ZoneState.Captured && _inZoneTeams.Count > 0 && !_inZoneTeams.ContainsValue(CurrentOccupyingTeam);

        toSend[i++] = _networkOccupyingTeamNumber;
        toSend[i++] = _networkOccupyingTeamProgress;
        toSend[i++] = _networkAdvantagedTeamNumber;
        toSend[i++] = _networkAdvantagedTeamProgress;
        toSend[i++] = _networkIsContested;
        toSend[i++] = _networkIsCapturing;
        toSend[i++] = _networkIsNeutralizing;
        return toSend;
    }

    public void Receive(object[] incoming)
    {
        int i = 0;
        SetZoneActive((bool) incoming[i++]);
        State = (ZoneState) incoming[i++];
        _networkOccupyingTeamNumber = (int) incoming[i++];
        _networkOccupyingTeamProgress = (float) incoming[i++];
        _networkAdvantagedTeamNumber = (int) incoming[i++];
        _networkAdvantagedTeamProgress = (float) incoming[i++];
        _networkIsContested = (bool) incoming[i++];
        _networkIsCapturing = (bool) incoming[i++];
        _networkIsNeutralizing = (bool) incoming[i++];
    }

    #endregion

    #region UNITY_MOETHODS

    private void Awake()
    {
        photonView = GetComponent<PhotonView>();

        GameMaster.OnMainInstanceSpawned(gm =>
        {
            _zoneControlData = GameMaster.instance.currentGameMode.data.zoneControlData;
            _scoreUpdatePeriod = 1f / _zoneControlData.scoreUpdateFrequency;
            _capturedZoneCaptureRate = 100f / _zoneControlData.capturedZoneCaptureTime;
            _neutralZoneCaptureRate = 100f / _zoneControlData.neutralZoneCaptureTime;
            this.InitializeNetworkComponent();

            _isGameMasterReady = true;
        });

        UserPlayer.OnMainInstanceSpawned(up =>
        {
            _myTeamNumber = UserPlayer.myInstance.teamNumber;

            _stateHudMarker = HUD.instance.CreateHUDMarker(hudMarkerNeutralSprite, Color.white, this);
            _stateHudMarker.gameObject.name = $"Zone [{photonView.ViewID}] State";
            _stateHudMarkerImage = _stateHudMarker.GetComponent<Image>();

            #region progress_bar

            /* Dev Note
             * The previous approach is to instantiate two GameObjects, for progress bar and progress bar background respectively,
             * and hard-wire the parent to _stateHudMarker, and finally set up the proper Image components as well as RectTransform configs.
             * However, this approach has two limitations:
             *  (1) Manually setting the parent of progress bar and progress bar background to _stateHudMarker (which is of type HUDMaker), 
             *      does not automatically turn on/off the visuals when _stateHudMarker should be off. This is because when HUDMarker is off, 
             *      only the Image component of it is disabled. There is no CanvasGroup component to it, and thus the children aren't turned
             *      off subsequently.
             *  (2) If we try to enable/disable the visuals of progress bar and progress bar background in methods such as Awake, OnEnabled, or
             *      OnDisabled, the non-host clients will encounter Null refs. This is because Photon destroys those objects on the non-host client
             *      side before re-instantiating these objects as these players join the game. And, we can't guarantee when these players will join
             *      and thus when _progressHudMarkerImage and _progressBackgroundImage will be cached on their machines. Sometimes, we will be trying
             *      to destroy these objects when they are null, leaving some additional progress bars and progress bar backgrounds in the middle of
             *      screen for those non-host clients.
             *
             * Before this approach was implemented and became obsolete, I tried creating three HUDMarkers for all three pieces: the state, the
             * progress bar, and the progress bar background. After that, I tried parenting the HUDMarkers of the progress bar and the background to 
             * that of the state. This also creates an issue where there will be positional offsets for the children HUDMarkers because they move on
             * the canvas depending on the player's viewing angle and their parent's position on the canvas which also depends on the player's viewing 
             * angle, meaning that we are doubling the effect of moving the HUDMarkers on the canvas. To reproduce this issue, try 
             * Transform.SetParent(_stateHudMarker.transform);
             * 
             * After these two trial and errors, I decided to simply create three HUDMarkers for each Zone and set them to the same hierarchical level.
             * In the future, we should allow HUDMarkers to have children HUDMarkers and turn them on/off using CanvasGroup. An alternative will be to
             * implement a different networking framework where the INetworkComponent objects won't be destroyed. Another alternative will be to wait 
             * for all players to join the game before the ZoneManager procedurally instantiate the Zones given a set of spawn points.
            */

            var bg = HUD.instance.CreateHUDMarker(progressCircleBackground, Color.white, this);
            bg.gameObject.name = $"Zone [{photonView.ViewID}] Background";
            _progressBackgroundImage = bg.GetComponent<Image>();
            var bgImageRT = bg.GetComponent<RectTransform>();
            bgImageRT.localScale = new Vector3(2f, 2f, 1f); //FIXME: scaling UI is bad

            var progressHudMarker = HUD.instance.CreateHUDMarker(progressCircleSprite, Color.white, this);
            progressHudMarker.gameObject.name = $"Zone [{photonView.ViewID}] Progress";
            _progressHudMarkerImage = progressHudMarker.GetComponent<Image>();
            _progressHudMarkerImage.type = Image.Type.Filled;
            _progressHudMarkerImage.fillMethod = Image.FillMethod.Radial360;
            _progressHudMarkerImage.fillOrigin = (int) Image.Origin360.Top;
            _progressHudMarkerImage.fillAmount = 0f;
            var progressRT = progressHudMarker.GetComponent<RectTransform>();
            progressRT.localScale = new Vector3(2f, 2f, 1f);

            #endregion

            #region text

            var stateTextChildGameObject = new GameObject($"Zone [{photonView.ViewID}] State Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            stateTextChildGameObject.transform.SetParent(_stateHudMarker.transform, false);
            stateTextChildGameObject.GetComponent<RectTransform>().localPosition = new Vector2(0f, 100f);
            _stateText = stateTextChildGameObject.GetComponent<TextMeshProUGUI>();
            _stateText.enableAutoSizing = true;
            _stateText.text = "";
            _stateText.material = Constants.instance.textMeshProOverlayMaterial;

            #endregion

            _isUserPlayerReady = true;
            _isHUDReady = true;
        });

        ZoneManager.OnMainInstanceSpawned(zm =>
        {
            if (!superZone)
            {
                ZoneManager.instance.RegisterAsInactive(this);
            }
        });
    }

    private void OnEnable()
    {
        #region Safeguarding

        Neutralize(false);
        _networkAdvantagedTeamNumber = int.MaxValue;
        _networkOccupyingTeamNumber = int.MaxValue;
        _networkAdvantagedTeamProgress = 0f;
        _networkOccupyingTeamProgress = 0f;

        #endregion
    }

    private void OnDestroy()
    {
        if (!superZone)
        {
            ZoneManager.instance?.DeregisterZone(this);
        }
    }

    protected override void Update()
    {
        base.Update();
        UpdateStateHudMarker();
        UpdateProgressHudMarker();
        UpdateZoneFieldColor();
        UpdateStateText();
    }

    protected override void OnPlayerEnter(APhysicsPlayer player)
    {
        base.OnPlayerEnter(player);
        if (this.IsSending())
        {
            RegisterPlayer(player);
        }
    }

    protected override void FixedUpdate()
    {
        base.FixedUpdate();
        if (this.IsSending())
        {
            //FIXME: potential GC issue
            // Equivalent to OnPlayerExit,
            // but also checks if player has been tackled inside
            HashSet<APhysicsPlayer> playersToRemove = new HashSet<APhysicsPlayer>();
            foreach (var p in _inZonePlayers)
            {
                if (!playersInArea.Contains(p) || p.GetSystem<ATackleSystem>()?.isTackled == true)
                {
                    playersToRemove.Add(p);
                }
            }

            foreach (var p in playersToRemove)
            {
                DeregisterPlayer(p);
            }

            //FIXME: wait for rb fix of playersInArea's auto-remove if not moving
            // Add back previously tackled players who remain in zone and also "revive" in zone
            foreach (var p in playersInArea)
            {
                if (!_inZonePlayers.Contains(p))
                {
                    RegisterPlayer(p);
                }
            }
        }

        UpdateCaptureMeter();
        UpdateScore();
    }

    #endregion

    #region PUBLIC_METHOD

    public void SudoNeutralize(bool findNextAdvantaged)
    {
        if (!superZone)
        {
            Neutralize(findNextAdvantaged);
        }
    }

    #endregion

    #region PRIVATE_METHODS

    private void RegisterPlayer(APhysicsPlayer player)
    {
        if (this.IsReceiving())
        {
            return;
        }

        _inZonePlayers.Add(player);

        if (_inZoneTeams.ContainsKey(player.teamNumber))
        {
            _inZoneTeams[player.teamNumber].AddMember(player);
        }
        else
        {
            // This happens when players of the advantaged team left and then come back to the zone
            // In this case, we don't want to "new" a ZoneTeam because it will be a different object and the progress bar will go backwards...
            // because in the UpdateCaptureMeter method we compare ZoneTeams instead of team numbers
            if (CurrentAdvantagedTeam != null && player.teamNumber == CurrentAdvantagedTeam.TeamNumber)
            {
                _inZoneTeams.Add(CurrentAdvantagedTeam.TeamNumber, CurrentAdvantagedTeam);
            }
            // This happens when players of the occupying team left and then come back to the zone
            else if (CurrentOccupyingTeam != null && player.teamNumber == CurrentOccupyingTeam.TeamNumber)
            {
                _inZoneTeams.Add(CurrentOccupyingTeam.TeamNumber, CurrentOccupyingTeam);
            }
            else
            {
                _inZoneTeams.Add(player.teamNumber, new ZoneTeam(player));
            }
        }

        //DebugHelper.LogGui($"Enter", 2f, this);
    }

    private void DeregisterPlayer(APhysicsPlayer player)
    {
        if (this.IsReceiving())
        {
            return;
        }

        _inZonePlayers.Remove(player);

        _inZoneTeams[player.teamNumber].RemoveMember(player);
        if (_inZoneTeams[player.teamNumber].Count <= 0)
        {
            _inZoneTeams.Remove(player.teamNumber);
        }

        //DebugHelper.LogGui($"Exit", 2f, this);
    }

    private void UpdateScore()
    {
        if (this.IsReceiving() || !_isReadyToUpdate)
        {
            return;
        }

        if (State == ZoneState.Captured)
        {
            if (IsContested && !_zoneControlData.updateScoreWhenContested)
            {
                return;
            }

            if (Time.time > _nextScoreUpdateTime)
            {
                _nextScoreUpdateTime = Time.time + _scoreUpdatePeriod / (1f + _zoneControlData.bonusScoreRatePerPlayer * CurrentOccupyingTeam.Count);
                AddPointsToTeam(CurrentOccupyingTeam.TeamNumber, _zoneControlData.pointsPerStep);
            }
        }

        void AddPointsToTeam(int teamNumber, int points)
        {
            UserPlayer.myInstance.GetSystem<ScoringSystem>().IncreaseScore(teamNumber, points);
        }
    }

    private void UpdateCaptureMeter()
    {
        if (this.IsReceiving() || !_isReadyToUpdate)
        {
            return;
        }

        if (State == ZoneState.Neutral)
        {
            if (IsContested)
            {
                return;
            }
            else if (_inZoneTeams.Count == 0)
            {
                // True neutral
                if (Mathf.RoundToInt(CaptureMeter) == 0)
                {
                    return;
                }

                if (CurrentAdvantagedTeam != null)
                {
                    CurrentAdvantagedTeam.PercentageInCurrentZone += _zoneControlData.emptyNeutralZoneMeterDrift * Time.fixedDeltaTime;
                }
            }
            else if (_inZoneTeams.Count == 1)
            {
                //FIXME: better way to get the only entry in a dictionary?
                ZoneTeam currentInZoneTeam = _inZoneTeams.FirstOrDefault().Value;

                if (CurrentAdvantagedTeam == null)
                {
                    CurrentAdvantagedTeam = currentInZoneTeam;
                }

                if (currentInZoneTeam == CurrentAdvantagedTeam)
                {
                    CurrentAdvantagedTeam.PercentageInCurrentZone += _neutralZoneCaptureRate * (1 + _zoneControlData.bonusCaptureRatePerPlayer * currentInZoneTeam.Count) * Time.fixedDeltaTime;
                }
                else
                {
                    CurrentAdvantagedTeam.PercentageInCurrentZone -= _neutralZoneCaptureRate * (1 + _zoneControlData.bonusCaptureRatePerPlayer * currentInZoneTeam.Count) * Time.fixedDeltaTime;

                    // Swap teams
                    if (CurrentAdvantagedTeam.PercentageInCurrentZone <= 0f)
                    {
                        CurrentAdvantagedTeam = currentInZoneTeam;
                    }
                }
            }

            if (CaptureMeter >= 100f)
            {
                Capture(CurrentAdvantagedTeam);
            }
        }
        else if (State == ZoneState.Captured)
        {
            if (IsContested)
            {
                // If the owning team is contesting against other team(s)
                if (_inZoneTeams.ContainsKey(CurrentOccupyingTeam.TeamNumber))
                {
                    return;
                }
                // If all in zone teams are non owning teams
                else
                {
                    CurrentOccupyingTeam.PercentageInCurrentZone -= _capturedZoneCaptureRate * (1 + _zoneControlData.bonusCaptureRatePerPlayer * _inZonePlayers.Count) * Time.fixedDeltaTime;
                }
            }
            else if (_inZoneTeams.Count == 0)
            {
                CurrentOccupyingTeam.PercentageInCurrentZone += _zoneControlData.emptyCapturedZoneMeterDrift * Time.fixedDeltaTime;
            }
            else if (_inZoneTeams.Count == 1)
            {
                //FIXME: better way to get the only entry in a dictionary
                ZoneTeam currentInZoneTeam = _inZoneTeams.FirstOrDefault().Value;

                if (currentInZoneTeam.TeamNumber == CurrentOccupyingTeam.TeamNumber)
                {
                    CurrentOccupyingTeam.PercentageInCurrentZone += _capturedZoneCaptureRate
                                                                    * (1f + _zoneControlData.bonusCaptureRatePerPlayer * currentInZoneTeam.Count)
                                                                    * (1f + _zoneControlData.friendlyRetakeRate) * Time.fixedDeltaTime;
                }
                else
                {
                    CurrentOccupyingTeam.PercentageInCurrentZone -= _capturedZoneCaptureRate * (1 + _zoneControlData.bonusCaptureRatePerPlayer * currentInZoneTeam.Count) * Time.fixedDeltaTime;
                }
            }

            if (CaptureMeter <= 0f)
            {
                Neutralize(true);
            }
        }
    }

    public void SetZoneActive(bool val)
    {
        zoneRenderer.gameObject.SetActive(val);
#if UNITY_EDITOR
        gameObject.name = "Zone " + (val ? "Active" : "Inactive");
#endif
    }

    private void Capture(ZoneTeam team)
    {
        State = ZoneState.Captured;
        CurrentOccupyingTeam = team;
    }

    private void Neutralize(bool findNextAdavantaged)
    {
        State = ZoneState.Neutral;
        CurrentOccupyingTeam = null;
        CurrentAdvantagedTeam = null;

        if (findNextAdavantaged)
        {
            StartCoroutine(FindNextAdvantagedTeam());
        }
    }

    private IEnumerator FindNextAdvantagedTeam()
    {
        int first = -1;
        int second = -2;
        ZoneTeam bestTeam = null;

        while (true)
        {
            foreach (ZoneTeam team in _inZoneTeams.Values)
            {
                if (team.Count > first)
                {
                    second = first;
                    first = team.Count;
                    bestTeam = team;
                }
                else if (team.Count > second)
                {
                    second = team.Count;
                }
            }

            if (first > 0 && first != second)
            {
                CurrentAdvantagedTeam = bestTeam;
                break;
            }

            yield return null;
        }
    }

    private void UpdateStateHudMarker()
    {
        if (!_isReadyToUpdate)
        {
            return;
        }

        if (State == ZoneState.Neutral)
        {
            _stateHudMarkerImage.sprite = hudMarkerNeutralSprite;

            // If has currently advantaged team
            if (_networkAdvantagedTeamNumber != int.MaxValue)
            {
                _stateHudMarkerImage.color = Avatar.myInstance.GetColorConfiguration(_networkAdvantagedTeamNumber).scoreboardColor;
            }
            else
            {
                _stateHudMarkerImage.color = Color.white;
            }
        }
        else if (State == ZoneState.Captured)
        {
            _stateHudMarkerImage.sprite = _networkOccupyingTeamNumber == _myTeamNumber ? hudMarkerFriendlySprite : hudMarkerEnemySprite;


            if (_networkOccupyingTeamNumber != int.MaxValue)
            {
                _stateHudMarkerImage.color = Avatar.myInstance.GetColorConfiguration(_networkOccupyingTeamNumber).scoreboardColor;
            }
        }
    }

    private void UpdateProgressHudMarker()
    {
        if (!_isReadyToUpdate)
        {
            return;
        }

        if (State == ZoneState.Neutral)
        {
            _progressHudMarkerImage.fillClockwise = _networkAdvantagedTeamNumber == _myTeamNumber;
            _progressHudMarkerImage.color = Color.white;
            _progressBackgroundImage.color = Color.white;
        }
        else if (State == ZoneState.Captured)
        {
            _progressHudMarkerImage.fillClockwise = _networkOccupyingTeamNumber == _myTeamNumber;
            if (_networkOccupyingTeamNumber != int.MaxValue)
            {
                _progressHudMarkerImage.color = Avatar.myInstance.GetColorConfiguration(_networkOccupyingTeamNumber).scoreboardColor;
                _progressBackgroundImage.color = Avatar.myInstance.GetColorConfiguration(_networkOccupyingTeamNumber).scoreboardColor;
            }
        }

        _progressHudMarkerImage.fillAmount = Mathf.Lerp(0f, 1f, CaptureMeter * 0.01f);
    }

    private void UpdateStateText()
    {
        if (!_isReadyToUpdate)
        {
            return;
        }

        if (_networkIsContested)
        {
            _stateText.color = Color.red;
            _stateText.text = "CONTESTED";
        }
        else if (_networkIsCapturing)
        {
            if (_networkAdvantagedTeamNumber != int.MaxValue)
            {
                _stateText.color = Avatar.myInstance.GetColorConfiguration(_networkAdvantagedTeamNumber).scoreboardColor;
            }

            _stateText.text = "CAPTURING";
        }
        else if (_networkIsNeutralizing)
        {
            if (_networkOccupyingTeamNumber != int.MaxValue)
            {
                _stateText.color = Avatar.myInstance.GetColorConfiguration(_networkOccupyingTeamNumber).scoreboardColor;
            }

            _stateText.text = "NEUTRALIZING";
        }
        else
        {
            _stateText.color = Color.clear;
        }
    }


    private void UpdateZoneFieldColor()
    {
        _zoneRenderers ??= GetComponentsInChildren<Renderer>(true);

        if (!_isReadyToUpdate)
        {
            return;
        }

        if (State == ZoneState.Neutral)
        {
            if (_networkIsCapturing && _networkAdvantagedTeamNumber != int.MaxValue)
            {
                foreach (var zr in _zoneRenderers)
                {
                    zr.material.color = PingPongColorLerp(_networkAdvantagedTeamNumber);
                }
            }
            else
            {
                foreach (var zr in _zoneRenderers)
                {
                    Color neutralZoneColor = Color.white;
                    neutralZoneColor.a = _fieldAlpha;
                    zr.material.color = neutralZoneColor;
                }
            }
        }
        else
        {
            if (_networkIsNeutralizing && _networkOccupyingTeamNumber != int.MaxValue)
            {
                foreach (var zr in _zoneRenderers)
                {
                    zr.material.color = PingPongColorLerp(_networkOccupyingTeamNumber);
                }
            }
            // Right after the zone being captured, there will be one frame where CurrentOccupyingTeam is null and _networkOccupyingTeamNumber is int.MaxValue.
            // So check against them before updating the color, otherwise we get an IndexOutOfRange exception.
            else if (_networkOccupyingTeamNumber != int.MaxValue)
            {
                foreach (var zr in _zoneRenderers)
                {
                    Color occupyingTeamColor = Avatar.myInstance.GetColorConfiguration(_networkOccupyingTeamNumber).scoreboardColor;
                    occupyingTeamColor.a = _fieldAlpha;
                    zr.material.color = occupyingTeamColor;
                }
            }
        }

        Color PingPongColorLerp(int teamNumber)
        {
            Color tc = Avatar.myInstance.GetColorConfiguration(teamNumber).scoreboardColor;
            Color oc = Color.white;
            tc.a = _fieldAlpha;
            oc.a = _fieldAlpha;
            Color result = Color.Lerp(tc, oc, Mathf.PingPong(Time.time * _zoneBlinkingSpeed, 1f));
            return result;
        }
    }

    #endregion
}

public enum ZoneState : byte
{
    Neutral,
    Captured
}

public class ZoneTeam
{
    public int Count => _inZoneMembers.Count;
    public int TeamNumber { get; set; } = int.MaxValue;

    public float PercentageInCurrentZone
    {
        get { return _percentageInCurrentZone; }
        set { _percentageInCurrentZone = Mathf.Clamp(value, 0f, 100f); }
    }

    private HashSet<APhysicsPlayer> _inZoneMembers = new HashSet<APhysicsPlayer>();
    private float _percentageInCurrentZone = 0f;

    public ZoneTeam(APhysicsPlayer initialPlayer)
    {
        AddMember(initialPlayer);
        TeamNumber = initialPlayer.teamNumber;
    }

    public void AddMember(APhysicsPlayer player) => _inZoneMembers.Add(player);
    public void RemoveMember(APhysicsPlayer player) => _inZoneMembers.Remove(player);
}