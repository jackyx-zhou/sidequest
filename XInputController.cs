using System.Runtime.InteropServices;

namespace Sidequest;

internal sealed class XInputController
{
    private const string Library = "xinput1_4.dll";
    private const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    private const ushort Start = 0x0010, Back = 0x0020, LeftShoulder = 0x0100, RightShoulder = 0x0200;
    private const ushort A = 0x1000, B = 0x2000;
    private uint _previousButtons;
    private DateTime _nextMove = DateTime.MinValue;

    public event Action<int>? Move;
    public event Action? Activate, BackPressed, Previous, Next;

    public void Poll()
    {
        try
        {
            for (uint slot = 0; slot < 4; slot++)
            {
                if (XInputGetState(slot, out var state) != 0) continue;
                var buttons = state.Gamepad.Buttons;
                var rising = (ushort mask) => (buttons & mask) != 0 && (_previousButtons & mask) == 0;
                if (rising(A)) Activate?.Invoke();
                if (rising(B) || rising(Back)) BackPressed?.Invoke();
                if (rising(LeftShoulder)) Previous?.Invoke();
                if (rising(RightShoulder)) Next?.Invoke();
                var direction = 0;
                if ((buttons & DpadUp) != 0 || state.Gamepad.ThumbLY > 15000) direction = -1;
                else if ((buttons & DpadDown) != 0 || state.Gamepad.ThumbLY < -15000) direction = 1;
                else if ((buttons & DpadLeft) != 0 || state.Gamepad.ThumbLX < -15000) direction = -2;
                else if ((buttons & DpadRight) != 0 || state.Gamepad.ThumbLX > 15000) direction = 2;
                if (direction != 0 && (DateTime.UtcNow >= _nextMove || direction != _heldDirection))
                {
                    Move?.Invoke(direction);
                    _nextMove = DateTime.UtcNow.AddMilliseconds(180);
                }
                _heldDirection = direction;
                _previousButtons = buttons;
                return;
            }
            _previousButtons = 0;
            _heldDirection = 0;
        }
        catch (DllNotFoundException) { }
    }

    private int _heldDirection;

    [StructLayout(LayoutKind.Sequential)] private struct State { public uint PacketNumber; public Gamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)] private struct Gamepad
    {
        public ushort Buttons; public byte LeftTrigger, RightTrigger; public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }
    [DllImport(Library, EntryPoint = "XInputGetState", ExactSpelling = true)] private static extern uint XInputGetState(uint userIndex, out State state);
}
