using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vortice.DirectInput;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// Active FFB probe. Mirrors what the game does at InputDriverPC.cpp:9056
/// (DInputFFB::FFBInit) so we can correlate HRESULTs across tools.
///
/// Walks the user through one effect at a time:
///   1. Try to create the effect (record HRESULT)
///   2. If created, start it and ask the user "did you feel it?"
///   3. Record the answer, stop the effect, move to next
///
/// This is the single most actionable diagnostic for the "FFB doesn't work"
/// cases because it captures both the kernel-side rejection (CreateEffect
/// HRESULT) and the human-side confirmation in one place.
/// </summary>
public sealed class FfbProbeService
{
    private readonly DirectInputService _di;
    private IDirectInputDevice8? _device;
    private readonly List<IDirectInputEffect> _liveEffects = new();
    private bool _exclusiveAcquired;

    public FfbProbeService(DirectInputService di) => _di = di;

    public bool BeginExclusive(Guid instanceGuid, IntPtr windowHandle, FfbProbeResult result)
    {
        _device = _di.GetDevice(instanceGuid);
        if (_device == null)
        {
            result.AcquireHResult = unchecked((int)0x80004005); // E_FAIL
            return false;
        }

        try { _device.Unacquire(); } catch { }
        try
        {
            _device.SetCooperativeLevel(windowHandle, CooperativeLevel.Exclusive | CooperativeLevel.Background);
        }
        catch (Exception ex)
        {
            result.AcquireHResult = ex.HResult;
            result.ExclusiveAcquireOk = false;
            return false;
        }

        try
        {
            var hr = _device.Acquire();
            result.AcquireHResult = hr.Code;
            result.ExclusiveAcquireOk = hr.Success;
        }
        catch (Exception ex)
        {
            result.AcquireHResult = ex.HResult;
            result.ExclusiveAcquireOk = false;
            return false;
        }

        // AUTOCENTER off mirrors what InputDriverPC does to clear any wheel
        // pull during constant-force testing.
        try
        {
            _device.Properties.AutoCenter = false;
            result.AutoCenterSetOk = true;
            result.AutoCenterHResult = 0;
        }
        catch (Exception ex)
        {
            result.AutoCenterSetOk = false;
            result.AutoCenterHResult = ex.HResult;
        }

        _exclusiveAcquired = result.ExclusiveAcquireOk;
        return result.ExclusiveAcquireOk;
    }

    public async Task<FfbEffectResult> TestConstantForceAsync(int magnitude, int durationMs, CancellationToken ct)
    {
        var r = new FfbEffectResult { EffectName = "ConstantForce" };
        if (_device == null || !_exclusiveAcquired)
        {
            r.HResult = unchecked((int)0x8000FFFFu);
            r.Note = "device not acquired exclusively";
            return r;
        }

        try
        {
            var effect = new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = durationMs * 1000, // microseconds
                Gain = 10000,
                SamplePeriod = 0,
                StartDelay = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                Axes = new int[] { 0 },
                Directions = new int[] { 0 },
                Envelope = null,
                Parameters = new ConstantForce { Magnitude = magnitude },
            };

            var fx = _device.CreateEffect(EffectGuid.ConstantForce, effect);
            if (fx == null)
            {
                r.HResult = unchecked((int)0x80004001u); // E_NOTIMPL
                r.CreateSucceeded = false;
                return r;
            }

            r.CreateSucceeded = true;
            _liveEffects.Add(fx);
            fx.Start(1, EffectPlayFlags.None);
            try
            {
                await Task.Delay(durationMs, ct);
            }
            finally
            {
                try { fx.Stop(); } catch { }
            }
        }
        catch (Exception ex)
        {
            r.HResult = ex.HResult;
            r.Note = ex.Message;
        }

        return r;
    }

    public async Task<FfbEffectResult> TestSpringAsync(int durationMs, CancellationToken ct)
    {
        var r = new FfbEffectResult { EffectName = "Spring" };
        if (_device == null || !_exclusiveAcquired)
        {
            r.HResult = unchecked((int)0x8000FFFFu);
            return r;
        }

        try
        {
            var p = new ConditionSet
            {
                Conditions = new[]
                {
                    new Condition
                    {
                        DeadBand = 0,
                        Offset = 0,
                        NegativeCoefficient = 10000,
                        PositiveCoefficient = 10000,
                        NegativeSaturation = 10000,
                        PositiveSaturation = 10000,
                    }
                }
            };

            var fx = _device.CreateEffect(EffectGuid.Spring, new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = durationMs * 1000,
                Gain = 10000,
                SamplePeriod = 0,
                TriggerButton = -1,
                Axes = new[] { 0 },
                Directions = new[] { 0 },
                Parameters = p,
            });

            if (fx == null) { r.HResult = unchecked((int)0x80004001u); return r; }
            r.CreateSucceeded = true;
            _liveEffects.Add(fx);
            fx.Start(1, EffectPlayFlags.None);
            try { await Task.Delay(durationMs, ct); }
            finally { try { fx.Stop(); } catch { } }
        }
        catch (Exception ex)
        {
            r.HResult = ex.HResult;
            r.Note = ex.Message;
        }

        return r;
    }

    public async Task<FfbEffectResult> TestDamperAsync(int durationMs, CancellationToken ct)
    {
        var r = new FfbEffectResult { EffectName = "Damper" };
        if (_device == null || !_exclusiveAcquired) { r.HResult = unchecked((int)0x8000FFFFu); return r; }

        try
        {
            var p = new ConditionSet
            {
                Conditions = new[]
                {
                    new Condition
                    {
                        DeadBand = 0,
                        Offset = 0,
                        NegativeCoefficient = 10000,
                        PositiveCoefficient = 10000,
                        NegativeSaturation = 10000,
                        PositiveSaturation = 10000,
                    }
                }
            };
            var fx = _device.CreateEffect(EffectGuid.Damper, new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = durationMs * 1000,
                Gain = 10000,
                SamplePeriod = 0,
                TriggerButton = -1,
                Axes = new[] { 0 },
                Directions = new[] { 0 },
                Parameters = p,
            });
            if (fx == null) { r.HResult = unchecked((int)0x80004001u); return r; }
            r.CreateSucceeded = true;
            _liveEffects.Add(fx);
            fx.Start(1, EffectPlayFlags.None);
            try { await Task.Delay(durationMs, ct); }
            finally { try { fx.Stop(); } catch { } }
        }
        catch (Exception ex)
        {
            r.HResult = ex.HResult;
            r.Note = ex.Message;
        }
        return r;
    }

    public async Task<FfbEffectResult> TestSineAsync(int durationMs, int periodMs, CancellationToken ct)
    {
        var r = new FfbEffectResult { EffectName = "Sine (vibration)" };
        if (_device == null || !_exclusiveAcquired) { r.HResult = unchecked((int)0x8000FFFFu); return r; }

        try
        {
            var p = new PeriodicForce
            {
                Magnitude = 10000,
                Offset = 0,
                Phase = 0,
                Period = periodMs * 1000,
            };
            var fx = _device.CreateEffect(EffectGuid.Sine, new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = durationMs * 1000,
                Gain = 10000,
                SamplePeriod = 0,
                TriggerButton = -1,
                Axes = new[] { 0 },
                Directions = new[] { 0 },
                Parameters = p,
            });
            if (fx == null) { r.HResult = unchecked((int)0x80004001u); return r; }
            r.CreateSucceeded = true;
            _liveEffects.Add(fx);
            fx.Start(1, EffectPlayFlags.None);
            try { await Task.Delay(durationMs, ct); }
            finally { try { fx.Stop(); } catch { } }
        }
        catch (Exception ex)
        {
            r.HResult = ex.HResult;
            r.Note = ex.Message;
        }
        return r;
    }

    public void StopAndRelease()
    {
        foreach (var fx in _liveEffects)
        {
            try { fx.Stop(); } catch { }
            try { fx.Dispose(); } catch { }
        }
        _liveEffects.Clear();

        if (_device != null)
        {
            try { _device.Properties.AutoCenter = true; } catch { }
            try { _device.Unacquire(); } catch { }
        }
        _device = null;
        _exclusiveAcquired = false;
    }
}
