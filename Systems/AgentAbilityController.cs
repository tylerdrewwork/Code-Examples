namespace AIW.Agent
{
    public enum ActionBufferType
    {
        Discrete,
        Continuous,
        Multi
    }

    /// <summary>
    /// The AgentAbilityController class is responsible for managing the abilities of an AI agent.
    /// It provides functionality for activating abilities, processing incoming action buffers, and managing ability prefabs.
    /// </summary>
    [ExecuteInEditMode, ShowOdinSerializedPropertiesInInspector]
    public class AgentAbilityController : MonoBehaviour, ISerializationCallbackReceiver, ISupportsPrefabSerialization
    {
        [SerializeField] private bool debugInConsole;

        [OnValueChanged("Odin_ToggleHeuristicGUIDebug")] [SerializeField]
        private bool debugInGUI;

        [SerializeField] private bool autoUpdateActionSpec;

        private void Odin_ToggleHeuristicGUIDebug()
        {
            AbilityControllerHeuristicDebug heuristicDebug = GetComponent<AbilityControllerHeuristicDebug>();
            if (debugInGUI && !heuristicDebug)
            {
                gameObject.AddComponent<AbilityControllerHeuristicDebug>();
                return;
            }

            if (!debugInGUI && heuristicDebug)
            {
                DestroyImmediate(heuristicDebug);
                return;
            }
        }


        // The AI agent controller
        private AIWAgentController _agentController; // Backing field

        public AIWAgentController AgentController
        {
            get
            {
                if (_agentController == null)
                {
                    _agentController = GetComponentInParent<AIWAgentController>(true);
                }

                return _agentController;
            }
            private set { _agentController = value; }
        }

        [ReadOnly] public readonly AbilityMediator AbilityMediator = new AbilityMediator();

        private bool Odin_InheritsAgentAbility(GameObject obj)
        {
            return obj.GetComponent<AgentAbility>() != null;
        }

        /// <summary>
        /// Backing field for Abilities. This is for best serialization practices so we don't lose the state of abilities on serialization.
        /// </summary>
        [FormerlySerializedAs("_abilities")]
        [SerializeField]
        [PropertySpace(spaceBefore: 20)]
        [ListDrawerSettings(DraggableItems = false, ShowFoldout = false, HideAddButton = true,
            OnBeginListElementGUI = "BeginAbilityElement",
            OnEndListElementGUI = "EndAbilityElement")]
        [OnCollectionChanged(Before = "AbilitiesChanged")]
        [VerticalGroup("Abilities"), PropertyOrder(0)]
        private List<ActionAbilityLink> _abilityLinks = new List<ActionAbilityLink>();

        public List<ActionAbilityLink> AbilityLinks => _abilityLinks;


        public void Initialize()
        {
            // Order abilities list by buffer index
            SortAbilities();

            foreach (var ability in AbilityLinks)
            {
                ability.Ability.SetAbilityMediator(AbilityMediator);
                ability.Ability.SetAgent(GetComponentInParent<AIWAgentController>(true));

                AbilityMediator.RegisterAbility(ability.Ability);

                SystemsInitHelper.Try("AbilityController.Ability.Initialize", ability.Ability.Initialize);
            }
        }

        private void Start()
        {
            AgentController = GetComponentInParent<AIWAgentController>(true);
        }

        public void UpdateAgentBehaviorParameters()
        {
            if (AgentController == null || AgentController.BehaviorParameters == null)
                return;

            var behaviorParams = AgentController.BehaviorParameters;
            var brainParams = behaviorParams.BrainParameters;

            // Get all abilities
            // var abilities = GetComponentsInChildren<AgentAbility>();

            // Separate regular abilities from multi-abilities
            var regularAbilityLinks = AbilityLinks.Where(a => a.Ability is not AgentAbilityMulti).ToList();
            var multiAbilityLinks = AbilityLinks.Where(a => a.Ability is AgentAbilityMulti).ToList();

            // Calculate regular ability counts
            int baseDiscreteCount = regularAbilityLinks
                .Count(a => a.Ability.BufferType == ActionBufferType.Discrete);
            int baseContinuousCount = regularAbilityLinks
                .Count(a => a.Ability.BufferType == ActionBufferType.Continuous);

            // Prepare discrete branch sizes
            var discreteBranchSizes = new List<int>();

            // Add regular discrete abilities as individual branches
            foreach (var abilityLink in regularAbilityLinks.Where(a => a.Ability.BufferType == ActionBufferType.Discrete))
            {
                discreteBranchSizes.Add(abilityLink.Ability.BranchSize); // Use the ability's actual branch size
            }

            // Add multi-ability discrete branches
            foreach (var mabLink in multiAbilityLinks)
            {
                if (mabLink.Ability is AgentAbilityMulti multiAbility)
                {
                    foreach (var branch in multiAbility.MultiAbilityInfo.DiscreteBranches)
                    {
                        discreteBranchSizes.Add(branch.size);
                    }
                }
            }

            // Calculate total continuous actions
            int totalContinuousActions = CalculateTotalContinuousActions();
            // int totalContinuousActions = baseContinuousCount +
            //                              multiAbilityLinks.Sum(m => m.MultiAbilityInfo.ContinuousActionCount);

            // Update action specs with proper branch structure

            if (autoUpdateActionSpec)
            {
                brainParams.ActionSpec = new ActionSpec(
                    numContinuousActions: totalContinuousActions,
                    discreteBranchSizes: discreteBranchSizes.ToArray()
                );
            }

            // Set start indices for multi-abilities
            int discreteBranchIndex = baseDiscreteCount; // Skip regular abilities
            int continuousIndex = baseContinuousCount;

            foreach (var abilityLink in multiAbilityLinks)
            {
                if (abilityLink.Ability is AgentAbilityMulti multiAbility)
                {
                    // Set discrete start index (points to first branch of this multi-ability)
                    int discreteStart = discreteBranchIndex;

                    // Update indices
                    multiAbility.SetBufferIndices(
                        discreteStart: discreteStart,
                        continuousStart: continuousIndex
                    );

                    // Move indices forward
                    discreteBranchIndex += multiAbility.MultiAbilityInfo.NumDiscreteBranches;
                    continuousIndex += multiAbility.MultiAbilityInfo.NumContinuousActions;
                }
            }
        }

        public void ActivateAbility(bool isDiscrete, int index, float value)
        {
            if (isDiscrete)
            {
                // TODO stop using linq here and find a more performant solution
                var dAbs = AbilityLinks.Where(abilityLink => abilityLink.actionBufferType == ActionBufferType.Discrete)
                    .ToList();
                dAbs[index].Ability.Activate((int)value); // cast as int for discrete
            }
            else
            {
                var cAbs = AbilityLinks
                    .Where(abilityLink => abilityLink.actionBufferType == ActionBufferType.Continuous)
                    .ToList();
                cAbs[index].Ability.Activate(value);
            }
        }

        /// <summary>
        /// Processes the incoming action buffers and activates the corresponding abilities.
        /// </summary>
        /// <param name="actionBuffers">The action buffers from BehaviorParameters component on the agent, containing the actions to be processed.</param>
        /// <param name="bufferData">The data associated with the action buffers.</param>
        public void ProcessIncomingActionBuffers(ActionBuffers actionBuffers, ActionBufferData bufferData)
        {
            int discreteIndex = 0;
            int continuousIndex = 0;

            // First process normal abilities
            foreach (var abilityLink in AbilityLinks.Where(a => a.Ability is not AgentAbilityMulti))
            {
                ProcessSingleAbility(
                    abilityLink,
                    actionBuffers,
                    ref discreteIndex,
                    ref continuousIndex,
                    bufferData.IsDisabled,
                    bufferData.IsRandomized
                );
            }

            // Then process multi-abilities
            foreach (var abilityLink in AbilityLinks.Where(a => a.Ability is AgentAbilityMulti))
            {
                ProcessMultiAbility(
                    (AgentAbilityMulti)abilityLink.Ability,
                    actionBuffers,
                    ref discreteIndex,
                    ref continuousIndex,
                    bufferData.IsDisabled,
                    bufferData.IsRandomized
                );
            }
        }

        private void ProcessMultiAbility(
            AgentAbilityMulti ability,
            ActionBuffers actions,
            ref int discreteIndex,
            ref int continuousIndex,
            bool actionsDisabled,
            bool actionsRandomized
        )
        {
            var info = ability.MultiAbilityInfo;
            int[] discreteValues = Array.Empty<int>();
            float[] continuousValues = Array.Empty<float>();

            // Discrete Branches
            if (info.NumDiscreteBranches > 0)
            {
                discreteValues = new int[info.NumDiscreteBranches];
                for (int i = 0; i < info.NumDiscreteBranches; i++)
                {
                    if (actionsDisabled)
                    {
                        // Force 0 when disabled
                        discreteValues[i] = 0;
                        discreteIndex++;
                    }
                    else if (actionsRandomized)
                    {
                        // Randomize within branch size
                        discreteValues[i] = UnityEngine.Random.Range(0, info.DiscreteBranches[i].size);
                        discreteIndex++;
                    }
                    else
                    {
                        // Use buffer value
                        discreteValues[i] = (int)actions.DiscreteActions[discreteIndex++];
                    }
                }
            }

            // Continuous Actions
            if (info.NumContinuousActions > 0)
            {
                continuousValues = new float[info.NumContinuousActions];
                for (int i = 0; i < info.NumContinuousActions; i++)
                {
                    if (actionsDisabled)
                    {
                        // Force 0 when disabled
                        continuousValues[i] = 0f;
                        continuousIndex++;
                    }
                    else if (actionsRandomized)
                    {
                        // Randomize between -1 and 1
                        continuousValues[i] = UnityEngine.Random.Range(-1f, 1f);
                        continuousIndex++;
                    }
                    else
                    {
                        // Use buffer value
                        continuousValues[i] = actions.ContinuousActions[continuousIndex++];
                    }
                }
            }

            if (!ability.active || !AgentController.Rig.Initialized) return;
            ability.ActivateDiscrete(discreteValues);
            ability.ActivateContinuous(continuousValues);
        }

        private void ProcessSingleAbility(
            ActionAbilityLink link,
            ActionBuffers actions,
            ref int discreteIndex,
            ref int continuousIndex,
            bool actionsDisabled,
            bool actionsRandomized
        )
        {
            // Discrete
            if (link.actionBufferType == ActionBufferType.Discrete)
            {
                int value;
                if (actionsDisabled)
                {
                    // Use 0 when actions are disabled
                    value = 0;
                    discreteIndex++; // Still increment index to maintain buffer position
                }
                else if (actionsRandomized)
                {
                    // Generate random value within branch size
                    value = UnityEngine.Random.Range(0, link.Ability.BranchSize);
                    discreteIndex++;
                }
                else
                {
                    // Use actual buffer value
                    value = (int)actions.DiscreteActions[discreteIndex++];
                }

                link.Ability.Activate(value);
            }
            // Continuous Ability
            else
            {
                float value;
                if (actionsDisabled)
                {
                    // Use 0 when disabled
                    value = 0f;
                    continuousIndex++;
                }
                else if (actionsRandomized)
                {
                    // Generate random value between -1 and 1
                    value = UnityEngine.Random.Range(-1f, 1f);
                    continuousIndex++;
                }
                else
                {
                    // Use actual buffer value
                    value = actions.ContinuousActions[continuousIndex++];
                }

                link.Ability.Activate(value);
            }
        }

        public int GetDiscreteAbilitiesCount()
        {
            return AbilityLinks.Count(a =>
                a.actionBufferType == ActionBufferType.Discrete);
        }

        public int[] GetDiscreteAbilitiesBranchSizes()
        {
            // For ML-Agents discrete actions, each branch represents a separate discrete action space
            // Since we're treating each discrete ability as its own branch (size=2 for on/off),
            // we return an array where each element is 2 (binary decision)
            return AbilityLinks
                .Where(a => a.actionBufferType == ActionBufferType.Discrete)
                .OrderBy(a => a.actionBufferIndex) // Sort by buffer index first
                .Select(a => a.Ability.BranchSize) // Let each ability define its size
                .ToArray();
        }

        public int CalculateTotalDiscreteBranches() =>
            AbilityLinks.Sum(a => a.Ability.AbilityInfo.NumDiscreteBranches);

        public int CalculateTotalContinuousActions() =>
            AbilityLinks.Sum(a => a.Ability.AbilityInfo.NumContinuousActions);

        // Add these new properties to track action counts
        public int CalculateBaseDiscreteBranches()
        {
            return GetComponentsInChildren<AgentAbility>(true)
                .Count(a => !(a is AgentAbilityMulti) && a.BufferType == ActionBufferType.Discrete);
        }

        public int CalculateBaseContinuousActions()
        {
            return GetComponentsInChildren<AgentAbility>(true)
                .Count(a => !(a is AgentAbilityMulti) && a.BufferType == ActionBufferType.Continuous);
        }

        public int CalculateTotalMultiDiscreteBranches()
        {
            return GetComponentsInChildren<AgentAbilityMulti>(true)
                .Sum(m => m.MultiAbilityInfo.DiscreteBranches.Length);
        }

        public int CalculateTotalMultiContinuousActions()
        {
            return GetComponentsInChildren<AgentAbilityMulti>(true)
                .Sum(m => m.MultiAbilityInfo.NumContinuousActions);
        }

        public int GetMultiAbilityCount()
        {
            return GetComponentsInChildren<AgentAbilityMulti>(true).Length;
        }

        public void UpdateIndices()
        {
            int discreteIdx = 0;
            int continuousIdx = 0;

            foreach (var ability in AbilityLinks)
            {
                if (ability.actionBufferType == ActionBufferType.Discrete)
                {
                    ability.actionBufferIndex = discreteIdx;
                    discreteIdx++;
                }
                else
                {
                    ability.actionBufferIndex = continuousIdx;
                    continuousIdx++;
                }
            }
        }

        private IEnumerable<int> GetMultiDiscreteBranchSizes(AgentAbilityMulti ability)
        {
            for (int i = 0; i < ability.MultiAbilityInfo.NumDiscreteBranches; i++)
            {
                yield return ability.MultiAbilityInfo.BranchSize;
            }
        }

        public int GetContinuousAbilitiesCount()
        {
            return AbilityLinks.Count(a =>
                a.actionBufferType == ActionBufferType.Continuous);
        }

        /// Gets a single agent ability of the specified type.
        /// <typeparam name="T">The type of agent ability to retrieve.</typeparam>
        public bool TryGetAgentAbilityOfType<T>(out T output) where T : AgentAbility
        {
            output = null;
            int count = 0;
            foreach (var abilityMapping in AbilityLinks)
            {
                if (abilityMapping.Ability is T ability)
                {
                    if (count == 0)
                    {
                        output = ability;
                    }

                    count++;
                }
            }

            if (count > 1)
            {
                Debug.LogWarning($"Multiple abilities of ability {typeof(T).Name} found. Only one was expected.");
            }

            return count > 0;
        }

        /// Gets a list of agent abilities of the specified type.
        /// <typeparam name="T">The type of agent ability to retrieve.</typeparam>
        public bool TryGetAgentAbilitiesOfType<T>(out List<T> output) where T : AgentAbility
        {
            output = new List<T>();
            foreach (var abilityMapping in AbilityLinks)
            {
                if (abilityMapping.Ability is T ability)
                {
                    output.Add(ability);
                }
            }

            if (output.Any()) return true;
            else return false;
        }


        #region Inspector & Editor

        // Odin Serialization
        [SerializeField, HideInInspector] private SerializationData serializationData = new();

        SerializationData ISupportsPrefabSerialization.SerializationData
        {
            get { return this.serializationData; }
            set { this.serializationData = value; }
        }

        /// <summary>
        /// Checks the agent's children for abilities and updates the <see cref="AbilityLinks"/> list accordingly.
        /// </summary>
        /// /// <remarks>
        /// This method removes any abilities from the <see cref="AbilityLinks"/> list that are no longer children of the agent,
        /// and adds any new abilities found in the children that are not already in the list.
        /// </remarks>
        /// 
        [OnInspectorGUI]
        [Button("Check and Add/Remove Abilities")]
        private void CheckAndAddRemoveAbilities()
        {
            var agentAbilities = GetComponentsInChildren<AgentAbility>(true);

            // Remove abilities that are in the list but not in the children
            AbilityLinks.RemoveAll(a => !agentAbilities.Contains(a.Ability));

            // Add abilities that are in the children but not in the list
            foreach (var agentAbility in agentAbilities)
            {
                if (AbilityLinks.All(a => a.Ability != agentAbility))
                {
                    var newActionAbility = new ActionAbilityLink(this)
                    {
                        Ability = agentAbility,
                        actionBufferIndex = AbilityLinks.Count
                    };
                    AbilityLinks.Add(newActionAbility);
                }
            }
        }

        // Validate Abilities Length
        // bool Odin_ValidateAbilitiesLength()
        // {
        //     int discreteCount = AgentController.GetBehaviorParameters().BrainParameters.ActionSpec.NumDiscreteActions;
        //     int continuousCount = AgentController.GetBehaviorParameters().BrainParameters.ActionSpec.NumContinuousActions;
        //     if (GetNumOfDiscreteAbilities() == discreteCount &&
        //         GetNumOfContinuousAbilities() == continuousCount) return true;
        //     else return false;
        // }

#if UNITY_EDITOR
        /// <summary>
        /// Odin Event handler for when the abilities collection changes.
        /// </summary>
        private void AbilitiesChanged(CollectionChangeInfo info, object value)
        {
            ActionAbilityLink actionAbility = AbilityLinks[info.Index];

            UpdateAgentBehaviorParameters();
            //

            // // Removing item from the list
            if (info.ChangeType == CollectionChangeType.RemoveIndex)
            {
                // Register the destroy operation for undo
                Undo.RegisterCompleteObjectUndo(this, "Delete Ability");

                // Record the ability before destruction for undo
                var abilityToDestroy = actionAbility.Ability;
                Undo.RecordObject(this, "Delete Ability");

                // Destroy through undo system
                Undo.DestroyObjectImmediate(abilityToDestroy.gameObject);
            }
        }

#endif
        
        [OnInspectorInit]
        [ShowIf("@AbilityLinks.Count > 0")]
        [Button("Update Abilities"), VerticalGroup("Abilities"), PropertyOrder(1)]
        public void SortAbilities()
        {
            // First, sort by action buffer type, then secondary sort by index
            AbilityLinks.Sort((a, b) =>
            {
                if (a.actionBufferType == b.actionBufferType)
                {
                    return a.actionBufferIndex.CompareTo(b.actionBufferIndex);
                }
                else
                {
                    return a.actionBufferType.CompareTo(b.actionBufferType);
                }
            });

            // Make sure the aac is not null for each link
            foreach (var link in AbilityLinks)
            {
                if (link.agentAbilityController == null)
                    link.agentAbilityController = this;
            }

            UpdateAgentBehaviorParameters();
        }


#if UNITY_EDITOR
        private void BeginAbilityElement(int index)
        {
            // Draw Section Headers for each ability type
            if (index == 0 ||
                _abilityLinks[index].actionBufferType != _abilityLinks[index - 1].actionBufferType)
            {
                Color bgColor = _abilityLinks[index].actionBufferType switch
                {
                    ActionBufferType.Discrete => new Color(0.9f, 0.6f, 0.6f, 0.1f),
                    ActionBufferType.Continuous => new Color(0.6f, 0.6f, 0.9f, 0.1f),
                    _ => new Color(0.6f, 0.9f, 0.6f, 0.1f)
                };

                EditorGUI.DrawRect(GUILayoutUtility.GetRect(0, 2), bgColor);
                GUILayout.Space(2);

                string header = _abilityLinks[index].actionBufferType + " Abilities";
                EditorGUILayout.LabelField(header, EditorStyles.boldLabel);
                SirenixEditorGUI.DrawThickHorizontalSeparator();
            }
        }

        private void EndAbilityElement(int index)
        {
        }
#endif

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            try
            {
                // Only attempt deserialization if the current object is valid
                if (this != null)
                {
                    UnitySerializationUtility.DeserializeUnityObject(this, ref this.serializationData);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Deserialization Error: {e.Message}");
            }

            // Defensive initialization of critical fields
            if (_abilityLinks == null)
            {
                Debug.LogWarning("Abilities list was null after deserialization. Reinitializing.");
                _abilityLinks = new List<ActionAbilityLink>();
            }

            // Additional safety checks for existing abilities
            for (int i = _abilityLinks.Count - 1; i >= 0; i--)
            {
                if (_abilityLinks[i]?.Ability == null)
                {
                    Debug.LogWarning($"Removing invalid ability link at index {i}");
                    _abilityLinks.RemoveAt(i);
                }
            }
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            if (this == null) return;
            UnitySerializationUtility.SerializeUnityObject(this, ref this.serializationData);
        }

        #endregion
    }
}