using System;

namespace Unity.EditorXR
{
    public enum ControllerRole
    {
        Authoring,
        Utility,
    }

    [Flags]
    public enum ControllerRoleMask
    {
        None = 0,
        Authoring = 1 << 0,
        Utility = 1 << 1,
        Both = Authoring | Utility,
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public sealed class ControllerToolRolesAttribute : Attribute
    {
        public ControllerRoleMask supportedRoles { get; private set; }
        public ControllerRoleMask companionRoles { get; private set; }

        public ControllerToolRolesAttribute(ControllerRoleMask supportedRoles,
            ControllerRoleMask companionRoles = ControllerRoleMask.None)
        {
            this.supportedRoles = supportedRoles;
            this.companionRoles = companionRoles;
        }
    }

    public interface IControllerRoleAware
    {
        ControllerRole controllerRole { get; set; }
        bool isCompanion { get; set; }
    }

    static class ControllerRoleUtility
    {
        public static ControllerRoleMask ToMask(ControllerRole role)
        {
            return role == ControllerRole.Authoring ? ControllerRoleMask.Authoring : ControllerRoleMask.Utility;
        }

        public static void GetToolRoles(Type toolType, out ControllerRoleMask supported, out ControllerRoleMask companion)
        {
            var attributes = toolType.GetCustomAttributes(typeof(ControllerToolRolesAttribute), true);
            if (attributes.Length == 0)
            {
                supported = ControllerRoleMask.Both;
                companion = ControllerRoleMask.None;
                return;
            }

            var attribute = (ControllerToolRolesAttribute)attributes[0];
            supported = attribute.supportedRoles;
            companion = attribute.companionRoles;
        }
    }
}
