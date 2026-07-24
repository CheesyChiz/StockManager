using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
namespace ExplorersIcebox.Util;

public static class AddonHelper
{
    public static unsafe bool IsAddonActive(string AddonName) // Used to see if the addon is active/ready to be fired on
    {
        return TryGetActiveAddon(AddonName, out _);
    }

    public static unsafe bool TryGetActiveAddon(string addonName, out AtkUnitBase* addon)
    {
        addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
        return addon != null && addon->IsVisible && addon->IsReady;
    }

    public static unsafe bool IsNodeVisible(string addonName, params int[] ids)
    {
        var ptr = Svc.GameGui.GetAddonByName(addonName);
        if (ptr == nint.Zero)
            return false;

        var addon = (AtkUnitBase*)ptr.Address;
        var node = GetNodeByIDChain(addon->GetRootNode(), ids);
        return node != null && node->IsVisible();
    }

    public static unsafe string GetNodeText(string addonName, params int[] nodeNumbers)
    {
        var ptr = Svc.GameGui.GetAddonByName(addonName);
        if (ptr == nint.Zero)
            return string.Empty;

        var addon = (AtkUnitBase*)ptr.Address;
        var uld = addon->UldManager;

        AtkResNode* node = null;
        for (var i = 0; i < nodeNumbers.Length; i++)
        {
            node = uld.NodeList[nodeNumbers[i]];

            if (i < nodeNumbers.Length - 1)
                uld = ((AtkComponentNode*)node)->Component->UldManager;
        }

        if (node->Type == NodeType.Counter)
            return ((AtkCounterNode*)node)->NodeText.ToString();

        var textNode = (AtkTextNode*)node;
        return textNode->NodeText.GetText();
    }
    public static unsafe AtkTextNode* GetAtkTextNode(string addonName, params int[] nodeNumbers)
    {
        var ptr = Svc.GameGui.GetAddonByName(addonName);
        if (ptr == nint.Zero)
            return null;

        var addon = (AtkUnitBase*)ptr.Address;
        var uld = addon->UldManager;

        AtkResNode* node = null;
        for (var i = 0; i < nodeNumbers.Length; i++)
        {
            node = uld.NodeList[nodeNumbers[i]];

            if (i < nodeNumbers.Length - 1)
                uld = ((AtkComponentNode*)node)->Component->UldManager;
        }

        var textNode = (AtkTextNode*)node;
        return textNode;
    }

    private static unsafe AtkResNode* GetNodeByIDChain(AtkResNode* node, params int[] ids)
    {
        if (node == null || ids.Length <= 0)
            return null;

        if (node->NodeId == ids[0])
        {
            if (ids.Length == 1)
                return node;

            List<int> newList = new(ids);
            newList.RemoveAt(0);

            var childNode = node->ChildNode;
            if (childNode != null)
                return GetNodeByIDChain(childNode, [.. newList]);

            if ((int)node->Type >= 1000)
            {
                var componentNode = node->GetAsAtkComponentNode();
                var component = componentNode->Component;
                var uldManager = component->UldManager;
                childNode = uldManager.NodeList[0];
                return childNode == null ? null : GetNodeByIDChain(childNode, [.. newList]);
            }

            return null;
        }

        //check siblings
        var sibNode = node->PrevSiblingNode;
        return sibNode != null ? GetNodeByIDChain(sibNode, ids) : null;
    }
}
