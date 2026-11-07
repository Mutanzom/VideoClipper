using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace VideoClipper.App;

/// <summary>A Grid that can change its mouse cursor (<c>UIElement.ProtectedCursor</c> is protected).</summary>
public sealed class CursorGrid : Grid
{
    public void SetCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
}
