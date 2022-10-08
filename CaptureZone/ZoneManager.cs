using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Photon.Pun;

[RequireComponent(typeof(PhotonView))]
public class ZoneManager : MonoSingleton<ZoneManager>, INetworkComponent
{
    private HashSet<Zone> _zones = new HashSet<Zone>();
    private HashSet<Zone> _activeZones = new HashSet<Zone>();
    private HashSet<Zone> _inactiveZones = new HashSet<Zone>();
    private ZoneControlData _zoneControlData;
    private ZoneCountValidity _validity = ZoneCountValidity.Invalid;

    private static Action<ZoneManager> _onSpawned;

    #region INetworkComponent
    public bool networkComponentInitialized { get; set; }
    public PhotonView photonView { get; set; }

    public SyncDirection syncDirection => PhotonNetwork.IsMasterClient ? SyncDirection.Send : SyncDirection.Receive;

    public bool syncOnInit => false;

    public float? syncRate => null;

    object[] toSend;
    public object[] Send(bool forceSync) => null;

    public void Receive(object[] incoming) { }
    #endregion

    #region UNITY_METHODS
    protected override void Awake()
    {
        base.Awake();
        _onSpawned.SafeInvoke(instance);
        _onSpawned = null;

        photonView = GetComponent<PhotonView>();
        GameMaster.OnMainInstanceSpawned(gm =>
        {
            this.InitializeNetworkComponent();
            _zoneControlData = GameMaster.instance.currentGameMode.data.zoneControlData;

            GenericCoroutineManager.instance.RunInSeconds(_zoneControlData.initialSpawnWaitTime, () =>
            {
                if (_zones.Count == 0 && _zoneControlData.numActiveControlZones > 0)
                {
                    Debug.LogWarning("[Zone Manager] No zone has been set up.");
                }
            }, this);
        });
    }
    #endregion

    #region PUBLIC_METHODS
    public static void OnMainInstanceSpawned(Action<ZoneManager> callback)
    {
        if (instance)
        {
            callback.SafeInvoke(instance);
        }
        else
        {
            _onSpawned += callback;
        }
    }

    protected void RegisterZone(Zone zone)
    {
        if (_zones.Count == 0)
        {
            //if zones are being registered now, you can initialize as soon they're all done
            GenericCoroutineManager.instance.RunAfterFrame(ZonesInitialization, this);
        }

        _zones.Add(zone);

    }

    public void DeregisterZone(Zone zone)
    {
        _zones.Remove(zone);
    }

    public void RegisterAsActive(Zone zone)
    {
        RegisterZone(zone);
        SetZoneActive(zone, true);
    }

    public void RegisterAsInactive(Zone zone)
    {
        RegisterZone(zone);
        SetZoneActive(zone, false);
    }
    #endregion

    #region PRIVATE_METHODS

    private void ZonesInitialization()
    {
        GameMaster.OnMainInstanceSpawned(gm =>
        {
            if (!this.IsSending())
            {
                return;
            }

            // Bail out; don't trigger coroutine
            if (_zoneControlData.numActiveControlZones <= 0)
            {
                foreach (var z in _zones)
                {
                    SetZoneActive(z, false);
                }
                return;
            }

            // If too few available zones, activate all of them. In OpenCloseZones(), just neutralize all of them
            if (_zones.Count <= _zoneControlData.numActiveControlZones)
            {
                _validity = ZoneCountValidity.Invalid;

                if (_zones.Count < _zoneControlData.numActiveControlZones)
                {
                    Debug.LogWarning($"[Zone Manager] Invalid zone count. Trying to activate {_zoneControlData.numActiveControlZones} zones while only {_zones.Count} zone(s) are setup. Recommended: {2 * _zoneControlData.numActiveControlZones} zones.");
                }
                else
                {
                    Debug.LogWarning($"[Zone Manager] Insufficient zone count. {_zones.Count} zone(s) have been set up. Recommanded: {2 * _zoneControlData.numActiveControlZones} zones.");
                }

                foreach (var z in _zones)
                {
                    SetZoneActive(z, true);
                }
            }
            else if (_zones.Count > _zoneControlData.numActiveControlZones && _zones.Count < 2 * _zoneControlData.numActiveControlZones)
            {
                _validity = ZoneCountValidity.Insufficient;
                Debug.LogWarning($"[Zone Manager] Insufficient zone count. {_zones.Count} zone(s) have been set up. Recommended: {2 * _zoneControlData.numActiveControlZones} zones.");

                foreach (var z in _zones)
                {
                    SetZoneActive(z, false);
                }
                Zone[] zonesToStartWith = GetRandomZonesFromSelection(_zones, _zoneControlData.numActiveControlZones);
                foreach (var z in zonesToStartWith)
                {
                    SetZoneActive(z, true);
                }
            }
            else
            {
                _validity = ZoneCountValidity.Valid;
                foreach (var z in _zones)
                {
                    SetZoneActive(z, false);
                }

                Zone[] zonesToStartWith = GetRandomZonesFromSelection(_zones, _zoneControlData.numActiveControlZones);
                foreach (var z in zonesToStartWith)
                {
                    SetZoneActive(z, true);
                }
            }

            // acitvate zones
            StartCoroutine(UpdateZones());
        });
    }

    private IEnumerator UpdateZones()
    {
        // Wait for _zoneControlData.initialSpawnWaitTime seconds to activate zones
        // Before that, deactivate all first
        foreach (Zone zone in _zones)
        {
            SetZoneActive(zone, false);
        }
        yield return GenericCoroutineManager.instance.WaitForSeconds(_zoneControlData.initialSpawnWaitTime);

        while (true)
        {
            OpenCloseZones();
            yield return GenericCoroutineManager.instance.WaitForSeconds(_zoneControlData.controlZoneMovePeriod);
        }
    }

    private void OpenCloseZones()
    {
        if (_validity == ZoneCountValidity.Invalid)
        {
            // Turn off then on all the zones to neutralize all
            foreach (Zone zone in _zones)
            {
                SetZoneActive(zone, false);
            }
            foreach (Zone zone in _zones)
            {
                SetZoneActive(zone, true);
            }
        }
        else if (_validity == ZoneCountValidity.Insufficient)
        {
            // Activate all currently inactive zones
            // Deactivate all currently active zones and re-activate a selection of them to meet numActiveControlZones
            //   (1) Cache all inactive zones to inactiveZonesToActivate
            //   (2) Cache a selection of active zones to activeZonesToReactivate
            //   (3) Deactivate all active zones
            //   (4) Activate zones in inactiveZonesToActivate and activeZonesToReactivate
            Zone[] inactiveZonesToActivate = _inactiveZones.ToArray();
            Zone[] activeZonesToReactivate = GetRandomZonesFromSelection(_activeZones, _zoneControlData.numActiveControlZones - _inactiveZones.Count);
            Zone[] zonesToDeactivate = _activeZones.ToArray();
            foreach (var z in zonesToDeactivate)
            {
                SetZoneActive(z, false);
            }
            foreach (var z in inactiveZonesToActivate)
            {
                SetZoneActive(z, true);
            }
            foreach (var z in activeZonesToReactivate)
            {
                SetZoneActive(z, true);
            }
        }
        else
        {
            // Activate a selection of currenly inactive zones
            // Deactivate all currently active zones
            //   (1) Cache a selection of inactive zones to activate
            //   (2) Deactivate every zone
            //   (3) Activate the cached zones

            Zone[] zonesToActivate = GetRandomZonesFromSelection(_inactiveZones, _zoneControlData.numActiveControlZones);
            Zone[] zonesToDeactivate = _activeZones.ToArray();
            foreach (var z in zonesToDeactivate)
            {
                SetZoneActive(z, false);
            }
            foreach (var z in zonesToActivate)
            {
                SetZoneActive(z, true);
            }
        }
    }

    private void SetZoneActive(Zone zone, bool toActivate)
    {
        zone.SetZoneActive(toActivate);
        if (toActivate)
        {
            if (_inactiveZones.Contains(zone))
            {
                _inactiveZones.Remove(zone);
            }

            _activeZones.Add(zone);
        }
        else
        {
            if (_activeZones.Contains(zone))
            {
                _activeZones.Remove(zone);
            }

            _inactiveZones.Add(zone);
            zone.SudoNeutralize(false);
        }
    }

    private Zone[] GetRandomZonesFromSelection(HashSet<Zone> selection, int amount)
    {
        if (amount > selection.Count || amount < 0)
        {
            Debug.LogError("Invalid amount specified. Wanted " + amount + " zones, but only " + selection.Count + " registered.");
        }

        System.Random random = new System.Random();
        Zone[] randomSelection = selection.OrderBy(x => random.Next()).ToArray();
        Zone[] result = new Zone[amount];

        // I can't think of better syntax sugar
        for (int i = 0; i < amount; i++)
        {
            result[i] = randomSelection[i];
        }

        return result;
    }
    #endregion
}

public enum ZoneCountValidity
{
    Invalid,
    Insufficient,
    Valid,
}
