using System.Runtime.InteropServices;
using System.Text;

namespace BTDeviceBattery.Interop;

/// <summary>
/// Minimal P/Invoke surface over cfgmgr32 for reading devnode properties.
/// </summary>
internal static class CfgMgr32
{
    internal const uint CR_SUCCESS = 0;
    internal const uint CR_BUFFER_SMALL = 0x1A;
    internal const uint CR_NO_SUCH_DEVINST = 0x0F;

    internal const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    internal const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;

    internal const uint DEVPROP_TYPE_BYTE = 0x00000003;
    internal const uint DEVPROP_TYPE_STRING = 0x00000012;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint ulLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint dnDevInst, string? pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint pdnParent, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DEVPROPKEY propertyKey, out uint propertyType,
        byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    /// <summary>Instance IDs of all devnodes, phantom included (there is no PHANTOM
    /// list flag: unfiltered enumerates the whole Enum registry key).</summary>
    internal static List<string> GetAllDeviceIds() => GetDeviceIds(0);

    /// <summary>Instance IDs of present devnodes only.</summary>
    internal static List<string> GetPresentDeviceIds() => GetDeviceIds(CM_GETIDLIST_FILTER_PRESENT);

    private static List<string> GetDeviceIds(uint filter)
    {
        var ids = new List<string>();
        if (CM_Get_Device_ID_List_SizeW(out var size, null, filter) != CR_SUCCESS || size == 0)
            return ids;

        var buffer = new char[size];
        if (CM_Get_Device_ID_ListW(null, buffer, size, filter) != CR_SUCCESS)
            return ids;

        // The buffer is a REG_MULTI_SZ: null-terminated strings, double null at the end.
        var start = 0;
        for (var i = 0; i < size; i++)
        {
            if (buffer[i] != '\0') continue;
            var len = i - start;
            if (len == 0) break;
            ids.Add(new string(buffer, start, len));
            start = i + 1;
        }
        return ids;
    }

    /// <summary>Present devnodes only; a phantom node answers CR_NO_SUCH_DEVINST.</summary>
    internal static bool IsPresent(uint devInst) =>
        CM_Get_DevNode_Status(out _, out _, devInst, 0) == CR_SUCCESS;

    internal static uint? LocateDevNode(string deviceId)
    {
        if (CM_Locate_DevNodeW(out var inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
            return null;
        return inst;
    }

    internal static uint? GetParent(uint devInst)
    {
        if (CM_Get_Parent(out var parent, devInst, 0) != CR_SUCCESS)
            return null;
        return parent;
    }

    internal static byte? GetByteProperty(uint devInst, in DEVPROPKEY key)
    {
        var k = key;
        uint size = 0;
        if (CM_Get_DevNode_PropertyW(devInst, ref k, out var type, null, ref size, 0) != CR_BUFFER_SMALL ||
            type != DEVPROP_TYPE_BYTE)
            return null;

        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref k, out type, buffer, ref size, 0) != CR_SUCCESS ||
            type != DEVPROP_TYPE_BYTE || size < 1)
            return null;

        return buffer[0];
    }

    internal static string? GetStringProperty(uint devInst, in DEVPROPKEY key)
    {
        var k = key;
        uint size = 0;
        if (CM_Get_DevNode_PropertyW(devInst, ref k, out var type, null, ref size, 0) != CR_BUFFER_SMALL ||
            type != DEVPROP_TYPE_STRING)
            return null;

        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref k, out type, buffer, ref size, 0) != CR_SUCCESS ||
            type != DEVPROP_TYPE_STRING)
            return null;

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }
}