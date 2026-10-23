using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.Fishing;
using DOL.GS.ServerProperties;
using DOL.Logging;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public class UT_FishingAggro
{
    private bool _previousSetting;
    private GameServer _previousServer;

    // GameObject's static constructor reads data quests from GameServer.Database, and GetUninitializedObject runs it,
    // so a server with an empty database must be in place before the first player is made.
    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();

    private sealed class InertServer : GameServer
    {
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }

    private sealed class Angler : GamePlayer
    {
        private Angler() : base(null, null) { }
        public eActiveWeaponSlot Slot;
        public bool Fighting;
        public override eActiveWeaponSlot ActiveWeaponSlot => Slot;
        public override bool InCombat => Fighting;
    }

    public class TwoHandInventory : DispatchProxy
    {
        public DbInventoryItem TwoHand;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(IGameInventory.GetItem) && args[0] is eInventorySlot.TwoHandWeapon)
            {
                return TwoHand;
            }

            return null;
        }
    }

    [OneTimeSetUp]
    public void InitializeLogging() => LoggerManager.InitializeWithExplicitLibrary(null, LogLibrary.None);

    [SetUp]
    public void SetUp()
    {
        _previousServer = GameServer.Instance;
        GameServer.LoadTestDouble((InertServer)RuntimeHelpers.GetUninitializedObject(typeof(InertServer)));
        _previousSetting = Properties.FISHING_POLE_PREVENTS_AGGRO;
        Properties.FISHING_POLE_PREVENTS_AGGRO = true;
    }

    [TearDown]
    public void TearDown()
    {
        Properties.FISHING_POLE_PREVENTS_AGGRO = _previousSetting;
        GameServer.LoadTestDouble(_previousServer);
    }

    private static Angler Create(string twoHandTemplateId, eActiveWeaponSlot slot = eActiveWeaponSlot.TwoHanded)
    {
        Angler player = (Angler)RuntimeHelpers.GetUninitializedObject(typeof(Angler));
        player.Slot = slot;

        IGameInventory inventory = DispatchProxy.Create<IGameInventory, TwoHandInventory>();

        if (twoHandTemplateId != null)
        {
            ((TwoHandInventory)(object)inventory).TwoHand = new DbInventoryItem { Template = new DbItemTemplate { Id_nb = twoHandTemplateId } };
        }

        player.Inventory = inventory;

        return player;
    }

    [Test]
    public void PlayerHoldingThePoleOutOfCombatIsLeftAlone()
    {
        Assert.That(FishingAggro.IsPeacefulAngler(Create(FishingCatalog.PoleTemplateId)), Is.True);
    }

    [Test]
    public void PoleSheathedOrSwitchedAwayIsNotPeaceful()
    {
        Assert.That(FishingAggro.IsPeacefulAngler(Create(FishingCatalog.PoleTemplateId, eActiveWeaponSlot.Standard)), Is.False);
        Assert.That(FishingAggro.IsPeacefulAngler(Create(FishingCatalog.PoleTemplateId, eActiveWeaponSlot.Distance)), Is.False);
    }

    [Test]
    public void OtherTwoHandedWeaponOrEmptySlotIsNotPeaceful()
    {
        Assert.That(FishingAggro.IsPeacefulAngler(Create("some_great_sword")), Is.False);
        Assert.That(FishingAggro.IsPeacefulAngler(Create(null)), Is.False);
    }

    [Test]
    public void AnglerInCombatIsAFairTarget()
    {
        Angler player = Create(FishingCatalog.PoleTemplateId);
        player.Fighting = true;

        Assert.That(FishingAggro.IsPeacefulAngler(player), Is.False);
    }

    [Test]
    public void AnglerSwingingThePoleIsAFairTarget()
    {
        Angler player = Create(FishingCatalog.PoleTemplateId);
        player.attackComponent = (AttackComponent)RuntimeHelpers.GetUninitializedObject(typeof(AttackComponent));
        player.attackComponent.AttackState = true;

        Assert.That(FishingAggro.IsPeacefulAngler(player), Is.False);
    }

    [Test]
    public void SettingOffRestoresNormalAggro()
    {
        Properties.FISHING_POLE_PREVENTS_AGGRO = false;

        Assert.That(FishingAggro.IsPeacefulAngler(Create(FishingCatalog.PoleTemplateId)), Is.False);
    }

    [Test]
    public void BotsAndNullAreNeverPeacefulAnglers()
    {
        // Companion bots and autonomous gamebots are both GameBot, which is a GameNPC rather than a GamePlayer.
        GameBot bot = (GameBot)RuntimeHelpers.GetUninitializedObject(typeof(GameBot));

        Assert.That(FishingAggro.IsPeacefulAngler(bot), Is.False);
        Assert.That(FishingAggro.IsPeacefulAngler(null), Is.False);
    }
}
