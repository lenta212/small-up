using Content.Shared.Construction.Prototypes;
using Content.Shared.DeviceLinking; // Mono
using Content.Shared.Lathe.Prototypes;
using Content.Shared.Research.Prototypes;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Lathe
{
    [RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true)]
    public sealed partial class LatheComponent : Component
    {
        /// <summary>
        /// All of the recipe packs that the lathe has by default
        /// </summary>
        [DataField]
        public List<ProtoId<LatheRecipePackPrototype>> StaticPacks = new();

        /// <summary>
        /// All of the recipe packs that the lathe is capable of researching
        /// </summary>
        [DataField]
        public List<ProtoId<LatheRecipePackPrototype>> DynamicPacks = new();

        /// <summary>
        /// The lathe's construction queue
        /// </summary>
        [DataField]
        public List<LatheRecipeBatch> Queue = new(); // Frontier: LatheRecipePrototype<LatheRecipeBatch

        /// <summary>
        /// Maximum number of not-yet-started products that may be queued.
        /// The product currently being fabricated is not included.
        /// </summary>
        [DataField, AutoNetworkedField]
        public int MaxQueuedItems = DefaultMaxQueuedItems;

        public const int DefaultMaxQueuedItems = 1000;

        /// <summary>
        /// The sound that plays when the lathe is producing an item, if any
        /// </summary>
        [DataField]
        public SoundSpecifier? ProducingSound;

        [DataField]
        public string? ReagentOutputSlotId;

        /// <summary>
        /// The default amount that's displayed in the UI for selecting the print amount.
        /// </summary>
        [DataField, AutoNetworkedField]
        public int DefaultProductionAmount = 1;

        #region Visualizer info
        [DataField]
        public string? IdleState;

        [DataField]
        public string? RunningState;

        [DataField]
        public string? UnlitIdleState;

        [DataField]
        public string? UnlitRunningState;
        #endregion

        /// <summary>
        /// The recipe the lathe is currently producing
        /// </summary>
        [ViewVariables]
        public LatheRecipePrototype? CurrentRecipe;

        #region MachineUpgrading
        /// <summary>
        /// A modifier that changes how long it takes to print a recipe
        /// </summary>
        [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
        [Access(typeof(SharedLatheSystem))] // Mono
        public float TimeMultiplier = 1;

        /// <summary>
        /// A modifier that changes how much of a material is needed to print a recipe
        /// </summary>
        [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
        [Access(typeof(SharedLatheSystem))] // Mono
        public float MaterialUseMultiplier = 1;

        /// <summary>
        /// A modifier that changes how long it takes to print a recipe
        /// </summary>
        [DataField, ViewVariables(VVAccess.ReadOnly), AutoNetworkedField]
        [Access(typeof(SharedLatheSystem))] // Mono
        public float FinalTimeMultiplier = 1;

        /// <summary>
        /// A modifier that changes how much of a material is needed to print a recipe
        /// </summary>
        [DataField, ViewVariables(VVAccess.ReadOnly), AutoNetworkedField]
        [Access(typeof(SharedLatheSystem))] // Mono
        public float FinalMaterialUseMultiplier = 1;

        public const float DefaultPartRatingMaterialUseMultiplier = 0.93f; // Frontier: restored for machine parts // Mono - nerf

        //Frontier Upgrade Code Restore
        /// <summary>
        /// The machine part that reduces how long it takes to print a recipe.
        /// </summary>
        [DataField]
        public ProtoId<MachinePartPrototype> MachinePartPrintSpeed = "Manipulator";

        /// <summary>
        /// The value that is used to calculate the modified <see cref="TimeMultiplier"/>
        /// </summary>
        [DataField]
        public float PartRatingPrintTimeMultiplier = 0.5f;

        /// <summary>
        /// The machine part that reduces how much material it takes to print a recipe.
        /// </summary>
        [DataField]
        public ProtoId<MachinePartPrototype> MachinePartMaterialUse = "MatterBin";

        // Frontier: restored for machine part upgrades
        /// <summary>
        /// The value that is used to calculate the modifier <see cref="MaterialUseMultiplier"/>
        /// </summary>
        [DataField]
        public float PartRatingMaterialUseMultiplier = DefaultPartRatingMaterialUseMultiplier;
        // End Frontier

        // Frontier: restored for machine part upgrades
        /// <summary>
        /// If not null, finite and non-negative, modifies values on spawned items
        /// </summary>
        [DataField]
        public float? ProductValueModifier = 1.2f; //0.3f->1.2f Mono
        // End Frontier
        #endregion

        // <Mono>
        /// <summary>
        /// Whether to add recipes back to the end of the queue after fabricating them.
        /// </summary>
        [DataField]
        public bool Loop = false;

        /// <summary>
        /// Whether to skip recipes if lacking resources, as opposed to waiting for resources.
        /// </summary>
        [DataField]
        public bool SkipBad = false;

        /// <summary>
        /// Whether the lathe is paused.
        /// Will stop it from advancing the queue, but will not stop production of current recipe.
        /// </summary>
        [DataField]
        public bool Paused = false;

        [DataField]
        public ProtoId<SinkPortPrototype> PausePort = "Pause";

        [DataField]
        public ProtoId<SinkPortPrototype> ResumePort = "Resume";

        [DataField]
        public ProtoId<SourcePortPrototype> ProducedPort = "Produced";
        // </Mono>
    }

    public sealed class LatheGetRecipesEvent : EntityEventArgs
    {
        public readonly EntityUid Lathe;

        public bool getUnavailable;

        public HashSet<ProtoId<LatheRecipePrototype>> Recipes = new();

        public LatheGetRecipesEvent(EntityUid lathe, bool forced)
        {
            Lathe = lathe;
            getUnavailable = forced;
        }
    }

    // Frontier: batch lathe recipes
    [Serializable, DataDefinition]
    public sealed partial class LatheRecipeBatch
    {
        private static int NextIndex = 0; // Mono
        public int Index; // Mono - for de-queuing recipes to work properly

        [DataField(required: true)]
        public LatheRecipePrototype Recipe;

        // Actor is transient attribution for the current server session. It
        // must not retain an entity reference across a full-grid restore.
        public NetEntity? Actor; // Mono - Log the person who queued the recipe.

        [DataField]
        public int ItemsPrinted;

        [DataField]
        public int ItemsRequested;

        public LatheRecipeBatch()
        {
            Recipe = default!;
            Index = NextIndex++; // Mono
        }

        public LatheRecipeBatch(LatheRecipePrototype recipe, int itemsPrinted, int itemsRequested,
            NetEntity? actor) : this() // Mono
        {
            Recipe = recipe;
            ItemsPrinted = itemsPrinted;
            ItemsRequested = itemsRequested;
            Actor = actor; // Mono
        }
    }
    // End Frontier

    /// <summary>
    /// Event raised on a lathe when it starts producing a recipe.
    /// </summary>
    [ByRefEvent]
    public readonly record struct LatheStartPrintingEvent(LatheRecipePrototype Recipe);
}
