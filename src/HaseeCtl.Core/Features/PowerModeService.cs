using HaseeCtl.Core.Hw;

namespace HaseeCtl.Core.Features;

/// <summary>Performance mode handling, ported from FnKey Features.SetPowerMode / OnChangePowerMode.</summary>
public sealed class PowerModeService(Dchu dchu, Capabilities caps)
{
    public PowerModeKind Current => (PowerModeKind)dchu.ReadAppByte(1, 1);

    /// <summary>FnKey.Features.SetPowerMode (Features.cs:865).</summary>
    public void Set(PowerModeKind mode)
    {
        dchu.WriteAppByte(1, 1, (byte)mode);
        if (mode != PowerModeKind.Performance)
        {
            dchu.SetWmi(121, 25, (uint)mode);
        }
        else
        {
            uint num = 2;
            if (dchu.ReadAppByte(1, 32) == 1)
            {
                if (DttCheck.IsInstalled) num |= 0x80;
                else dchu.WriteAppByte(1, 32, 0);
            }
            if (dchu.ReadAppByte(4, 8) == 1)
            {
                if (caps.Dtt)
                {
                    if (DttCheck.IsInstalled) num |= 0x40;
                    else
                    {
                        dchu.WriteAppByte(4, 8, 0);
                        dchu.WriteAppByte(1, 32, 0);
                        num = 2;
                    }
                }
                else num |= 0x40;
            }
            dchu.SetWmi(121, 25, num);
        }
    }

    /// <summary>EnergyStar side channel used when the firmware has no hardware P-state support.</summary>
    public void ApplyEnergyStar(PowerModeKind mode)
    {
        if (caps.HardwarePState) return;
        uint v = mode switch
        {
            PowerModeKind.PowerSaving => 1,
            PowerModeKind.Performance => 0,
            _ => 2,
        };
        dchu.SetWmi(79, v);
    }

    public bool TurboFanEnabled => dchu.ReadAppByte(4, 8) == 1;

    public void SetTurboFan(bool on)
    {
        dchu.WriteAppByte(4, 8, (byte)(on ? 1 : 0));
        if (!on) return;
        if (caps.Dtt && !DttCheck.IsInstalled) dchu.WriteAppByte(4, 8, 0);
    }
}
