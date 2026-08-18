using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ECommons.UIHelpers.AddonMasterImplementations;
public partial class AddonMaster
{
    public unsafe class AirShipExplorationDetail : AddonMasterBase<AtkUnitBase>
    {
        public AirShipExplorationDetail(nint addon) : base(addon) { }

        public AirShipExplorationDetail(void* addon) : base(addon) { }

        public AtkComponentButton* DeployButton => Addon->GetComponentButtonById(26);
        public AtkComponentButton* CancelButton => Addon->GetComponentButtonById(27);

        public override string AddonDescription { get; } = "Submersible/Airship exploration voyage details window";

        public void Deploy() => ClickButtonIfEnabled(DeployButton);
        public void Cancel() => ClickButtonIfEnabled(CancelButton);
    }
}