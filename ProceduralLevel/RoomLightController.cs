using UnityEngine;

public class RoomLightController : MonoBehaviour
{
    [SerializeField] private Light _light;

    private float _saturation;
    private float _value;

    private void Awake()
    {
        Color.RGBToHSV(_light.color, out float _, out _saturation, out _value);
    }

    public void SetColor(float hue) => _light.color = Color.HSVToRGB(hue, _saturation, _value);
    public void SetRange(float range) => _light.range = range;
    public void SetIntensity(float intensity) => _light.intensity = intensity;

    public void SetLightActive(bool toActivate)
    {
        _light.enabled = toActivate;
        _light.gameObject.SetActive(toActivate);
    }
}
