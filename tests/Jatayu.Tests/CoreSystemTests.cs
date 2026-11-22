// ==================================================================================================
// SPRINT 5a: Core Platform & Input Subsystem Unit Tests
// Tests EventBus pub/sub, Input polling, InputActions binding, and Settings serialization.
// ==================================================================================================

using System.Numerics;
using Jatayu.Core.Config;
using Jatayu.Core.Events;
using Jatayu.Core.Input;
using Jatayu.Core.Logging;
using Xunit;

namespace Jatayu.Tests;

public readonly struct TestCustomEvent
{
    public readonly int Value;
    public TestCustomEvent(int value) => Value = value;
}

public class CoreSystemTests
{
    [Fact]
    public void EventBus_PublishAndSubscribe_DeliversEventsWithoutAllocations()
    {
        int receivedValue = 0;
        int callCount = 0;

        Jatayu.Core.Events.EventHandler<TestCustomEvent> handler = (in TestCustomEvent evt) =>
        {
            receivedValue = evt.Value;
            callCount++;
        };

        EventBus.Subscribe(handler);

        // Warmup
        EventBus.Publish(new TestCustomEvent(42));
        Assert.Equal(42, receivedValue);
        Assert.Equal(1, callCount);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            EventBus.Publish(new TestCustomEvent(i));
        }
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(99, receivedValue);
        Assert.Equal(101, callCount);
        Assert.Equal(0, allocAfter - allocBefore);

        EventBus.Unsubscribe(handler);
        EventBus.Publish(new TestCustomEvent(999));
        Assert.Equal(99, receivedValue); // Not updated
    }

    [Fact]
    public void Input_KeyAndMousePolling_TracksStatesCorrectly()
    {
        Input.Reset();

        // Simulate Key Press
        Input.RecordKeyEvent((int)Key.Space, true);
        Assert.True(Input.IsKeyDown(Key.Space));
        Assert.True(Input.IsKeyPressed(Key.Space));

        // Advance frame
        Input.BeginFrame();
        Assert.True(Input.IsKeyDown(Key.Space));
        Assert.False(Input.IsKeyPressed(Key.Space)); // No longer first frame pressed

        // Release key
        Input.RecordKeyEvent((int)Key.Space, false);
        Assert.False(Input.IsKeyDown(Key.Space));
        Assert.True(Input.IsKeyReleased(Key.Space));

        // Mouse Delta & Scroll
        Input.RecordMouseMove(100f, 200f);
        Input.BeginFrame();
        Input.RecordMouseMove(150f, 220f);
        Input.RecordScroll(0f, 1f);

        Assert.Equal(new Vector2(150f, 220f), Input.MousePosition);
        Assert.Equal(new Vector2(0f, 1f), Input.ScrollDelta);

        Input.Reset();
    }

    [Fact]
    public void InputActions_DefaultMappings_ResolveCorrectly()
    {
        Input.Reset();
        InputActions.SetupDefaults();

        // When Space is pressed, "Jump" action should be active
        Input.RecordKeyEvent((int)Key.Space, true);
        Assert.True(InputActions.IsActionDown("Jump"));
        Assert.True(InputActions.IsActionPressed("Jump"));

        // When D is pressed, "MoveX" should return 1.0f
        Input.RecordKeyEvent((int)Key.D, true);
        Assert.Equal(1.0f, InputActions.GetAxis("MoveX"));

        // When A is pressed simultaneously, "MoveX" should cancel to 0.0f
        Input.RecordKeyEvent((int)Key.A, true);
        Assert.Equal(0.0f, InputActions.GetAxis("MoveX"));

        Input.Reset();
    }

    [Fact]
    public void Settings_SaveAndLoad_RoundTripsJsonConfig()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        try
        {
            var original = new Settings
            {
                ResolutionWidth = 2560,
                ResolutionHeight = 1440,
                VSync = true,
                FieldOfView = 90.0f,
                MouseSensitivity = 1.75f,
                ShadowQuality = "Ultra"
            };

            original.Save(tempPath);
            Assert.True(File.Exists(tempPath));

            var loaded = Settings.Load(tempPath);
            Assert.Equal(2560, loaded.ResolutionWidth);
            Assert.Equal(1440, loaded.ResolutionHeight);
            Assert.True(loaded.VSync);
            Assert.Equal(90.0f, loaded.FieldOfView);
            Assert.Equal(1.75f, loaded.MouseSensitivity);
            Assert.Equal("Ultra", loaded.ShadowQuality);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void Log_SetSink_ReceivesFormattedMessages()
    {
        string lastMessage = string.Empty;
        LogLevel lastLevel = LogLevel.Trace;

        Log.SetSink((level, msg) =>
        {
            lastLevel = level;
            lastMessage = msg;
        });

        Log.Info("Engine test message");
        Assert.Equal(LogLevel.Info, lastLevel);
        Assert.Contains("Engine test message", lastMessage);

        Log.SetSink(null);
    }
}
