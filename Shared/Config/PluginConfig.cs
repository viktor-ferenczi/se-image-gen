using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shared.Config;

public class PluginConfig : IPluginConfig
{
    public event PropertyChangedEventHandler PropertyChanged;

    private void SetValue<T>(ref T field, T value, [CallerMemberName] string propName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;

        OnPropertyChanged(propName);
    }

    private void OnPropertyChanged([CallerMemberName] string propName = "")
    {
        PropertyChangedEventHandler propertyChanged = PropertyChanged;
        if (propertyChanged == null)
            return;

        propertyChanged(this, new PropertyChangedEventArgs(propName));
    }

    private bool enabled = true;
    private string imageServerUrl = "http://127.0.0.1:1234";
    private int timeoutSeconds = 300;
    private int imagePixels = 512 * 512;
    private int steps = 8;
    private float cfgScale = 3f;
    private int maxPendingPerPlayer = 1;
    private int maxPendingTotal = 3;
    private int maxCharacters = 100000;
    private float minCellPixels = 2.88f;
    private bool dither = true;
    private bool writeCustomData = true;
    private ModerationConfig moderation = new ModerationConfig();

    public bool Enabled
    {
        get => enabled;
        set => SetValue(ref enabled, value);
    }

    public string ImageServerUrl
    {
        get => imageServerUrl;
        set => SetValue(ref imageServerUrl, value);
    }

    public int TimeoutSeconds
    {
        get => timeoutSeconds;
        set => SetValue(ref timeoutSeconds, value);
    }

    public int ImagePixels
    {
        get => imagePixels;
        set => SetValue(ref imagePixels, value);
    }

    public int Steps
    {
        get => steps;
        set => SetValue(ref steps, value);
    }

    public float CfgScale
    {
        get => cfgScale;
        set => SetValue(ref cfgScale, value);
    }

    public int MaxPendingPerPlayer
    {
        get => maxPendingPerPlayer;
        set => SetValue(ref maxPendingPerPlayer, value);
    }

    public int MaxPendingTotal
    {
        get => maxPendingTotal;
        set => SetValue(ref maxPendingTotal, value);
    }

    public int MaxCharacters
    {
        get => maxCharacters;
        set => SetValue(ref maxCharacters, value);
    }

    public float MinCellPixels
    {
        get => minCellPixels;
        set => SetValue(ref minCellPixels, value);
    }

    public bool Dither
    {
        get => dither;
        set => SetValue(ref dither, value);
    }

    public bool WriteCustomData
    {
        get => writeCustomData;
        set => SetValue(ref writeCustomData, value);
    }

    public ModerationConfig Moderation
    {
        get => moderation;
        set => SetValue(ref moderation, value);
    }
}
