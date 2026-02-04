public class RoomPhaseController : MonoBehaviour
{
    // Unity events used in the inspector for room-level events
    [InfoBox(
        "Unity events should be set sparingly! They can cause merge conflicts and scene bloat. Ask yourself, 'is there any way I can set this up in a script instead?'",
        InfoMessageType.None)]
    
    private bool preventingExcessiveEndAttemptCallsFlag;
    
    private RoomController room;
    private FallFromSkyController fallFromSkyController;

    private void Awake()
    {
        room = GetComponent<RoomController>();
        fallFromSkyController = GetComponent<FallFromSkyController>();
    }

    public Phases CurrentPhase { get; set; }
    public bool IsEndingRoom { get; private set; }
    private bool isInCooldown;
    public bool IsDoneWithFalling { get; private set; }
    /// <summary>
    /// Flag that skips the cooldown phase in recording. Does nothing in training.
    /// </summary>
    private bool resetImmediatelyFlag = false;
    
    private void RaiseEvent(Phases phase, RoomEventArgs eventArgs)
    {
        CurrentPhase = phase;
        PhasePublisher.RaiseEvent(phase, eventArgs);
    }

    public void RequestFlyIn()
    {
        IsDoneWithFalling = false;
        
        // Make sure fall controller is present
        if (!fallFromSkyController) fallFromSkyController = GetComponentInChildren<FallFromSkyController>();
        Debug.Assert(fallFromSkyController, "Room Controller tried to find FallFromSkyController in children but couldn't.", this);

        Timing.RunCoroutine(room._InitializeRoom(StartSequence));

        void StartSequence()
        {
            room.Helper.PauseTimer();
            
            // Play the fall from sky sequence, then start the room when finished
            fallFromSkyController.PlaySequence(() =>
            {
                Debug.Log("Fall from sky sequence finished! Starting room.");
                IsDoneWithFalling = true;
                RoomStart();
            });
        }
    }

    /// <summary>
    /// Called to start the room. This should be called when the previous
    /// room is completely done with it's transition and we are ready to
    /// move the agents to this room to begin the attempts.
    /// </summary>
    public void RequestStartRoom()
    {
        if (room.Helper.HasRoomStarted()) {
            Debug.Log("room has already started");
            return;
        }

        if (!room.HasInitialized)
        {
            Debug.Log("Phase Logic Error: trying to start room before it has initialized. \n" +
                      "Are you trying to start the room AND call initialization?");
            return;
        }
        

        IsDoneWithFalling = true; // Make sure falling is not an issue
        RoomStart();
    }

    /// <summary>
    /// Request that the room controller begins the process of ending the take
    /// </summary>
    /// <param name="resetImmediately">Should skip the cooldown phase?</param>
    public void RequestEndAttempt(bool resetImmediately = false)
    {
        if (preventingExcessiveEndAttemptCallsFlag) return;
        if (room.Helper.IsRoomEnded()) return;
        resetImmediatelyFlag = resetImmediately; // skip cooldown phase
        
        preventingExcessiveEndAttemptCallsFlag = true;
        CooldownStart();
    }

    public void MarkRoomAsEnding()
    {
        if (SessionType.IsRecording && room.GetIsDoneWithRetryFirstAttempt()) 
            IsEndingRoom = true;
    }
    
    private void StartFirstAttempt()
    {
        room.RoomDisabled = false;
       
        Timing.RunCoroutine(_ResetAttempt(false)); // dont end episode on starting first attempt
    }
    
    #region -----===== PHASES =====-----
    
    protected virtual void RoomStart()
    {
        // Run room start in a coroutine to make sure room initializes sequentially
        Timing.RunCoroutine(_RoomStartCoroutine());
    }
    
    /// <summary>
    /// Call RoomStart instead, this coroutine is called by RoomStart to help initialize the room if it hasn't already 
    /// </summary>
    private IEnumerator<float> _RoomStartCoroutine()    
    {
        // Initialize the room and then run the local function once finished
        Timing.RunCoroutine(room._InitializeRoom(() =>
        {
            // Make sure agents enabled
            foreach (var a in room.Agents)
            {
                a.Helper.EnableAgent();
                a.gameObject.SetActive(true);
            }
            
            // Raise the room start event for all subscribers
            RaiseEvent(Phases.RoomStart, new RoomEventArgs(Phases.RoomStart, room));


            StartFirstAttempt();
        }));

        yield break;
    }

    protected virtual void AttemptStart()
    {
        RaiseEvent(Phases.AttemptStart, new RoomEventArgs(Phases.AttemptStart, room));
        preventingExcessiveEndAttemptCallsFlag = false;
    }

    /// <summary>
    /// Begin ending the take and start the level end delay
    /// </summary>
    protected virtual void CooldownStart()
    {
        if (SessionType.IsRecording && !resetImmediatelyFlag) // should activate the cooldown
        {
            if (isInCooldown == true) return;
            StartCoroutine(StartLevelEndDelay());
            
            RaiseEvent(Phases.AttemptCooldown, new RoomEventArgs(Phases.AttemptCooldown, room));
        }
        else // Skip the cooldown phase completely
        {
            AttemptEnd();
        }
    }
    
    /// <summary>
    /// End the take and prepare to reset. Called automatically by CooldownStart
    /// </summary>
    protected virtual void AttemptEnd()
    {
        // After take ends, reset the room
        if (!IsEndingRoom)
        {
            // Sends signals to rooms via OnPhaseAttemptEnd(), which end the agent episodes
            //RaiseEvent(Phases.AttemptEnd, new RoomEventArgs(Phases.AttemptEnd, room)); // moved into _ResetAttempt
            // Calls methods to end ep for agents, reset resettables, reset agents, and start a new attempt
            Timing.RunCoroutine(_ResetAttempt());

            // So, fundamentally, if we are ending the episode before adding reward, then we can't use anything
            // in agents.EndAgentEpisode() to add reward, and it must instead be handled in the room event.

            // I think we should just move the iteration over end agents' episodes into the RoomController base class,
            // so that the inherited classes must run the logic before their OnPhaseAttemptEnd() method is called. This could be
            // done using a virtual method and base.OnPhaseAttemptEnd(), but that leaves room for the inherited class not to run the superclass
            // code, so IMO we can just use another public, non-override method in RoomController, that then calls
            // protected override OnPhaseAttemptEnd().
        }
        
        // End the room (RECORDING ONLY)
        else
        {
            if (SessionType.IsTraining) SessionType.ThrowTrainingNotAllowedException();
            
            // Raise the attempt ending event
            RaiseEvent(Phases.AttemptEnd, new RoomEventArgs(Phases.AttemptEnd, room));
            IsEndingRoom = false;
            
            RoomEnd();
        }
    }

    /// <summary>
    /// <b>RECORDING ONLY!</b><br/>
    /// Rooms will act like they are ending by triggering the RoomEndSequencer attached to the room controller.
    /// </summary>
    protected virtual void RoomEnd()
    {
        if (SessionType.IsTraining)
            SessionType.ThrowTrainingNotAllowedException("Something went horribly wrong! RoomEnd was called while training! Something is wrong with the roomphase flow.");
        
        // GetComponent<RoomEndSequencer>().EndRoomSequence();

        RaiseEvent(Phases.RoomEnd, new RoomEventArgs(Phases.RoomEnd, room));
    }
    
    // Begins the delay before the level ends for extra recording time
    private IEnumerator StartLevelEndDelay()
    {
        isInCooldown = true;
        yield return new WaitForSeconds(room.ExtraTime);
        isInCooldown = false;
        AttemptEnd();
    }
    
    #endregion
    
    
    public bool IsResetting { get; private set; }
    
    /// <summary>
    /// Reset the take, including the agents, room, and objects
    /// </summary>
    protected virtual IEnumerator<float> _ResetAttempt(bool resetEpisode = true)
    {
        IsResetting = true;
        bypassAgents(true);

        // TODO: Move the OnPhaseAttemptEnd logic involving teams and the resetEpisodeOnAllAgents logic into something
        // shared that knows what teams we are using.
        if (resetEpisode)
        {
            // Scoop up final rewards
            resetEpisodeOnAllAgents();
            
            // Runs OnPhaseAttemptEnd
            /// WARNING!!!
            /// This should NOT be moved out of this if block unless we have a way to track if _ResetAttempt is called before the first attempt starts.
            /// Right now this if block is being used as a way to skip resetting the episode while initializing rooms & training envs. This happens to 
            /// coincide with the same instances where AttemptEnd should be called. In other words, AttemptEnd should not be called in _ResetAttempt UNLESS
            /// the first attempt has already started (i.e. AttemptStart has been called for the first time.)
            RaiseEvent(Phases.AttemptEnd, new RoomEventArgs(Phases.AttemptEnd, room));
            
            yield return Timing.WaitForOneFrame; // REQUIRED for objects to disable and reset at the end of the attempt
        }
        

        resetResettables();
        
        resetAgents();
        
        room.ResetRoom();

        IsResetting = false;
        resetImmediatelyFlag = false;

        AttemptStart();
        
        bypassAgents(false);
        yield return 0;
        
        
        void resetEpisodeOnAllAgents()
        {
            foreach (AIWAgentController agent in room.Agents)
            {
                if (!agent.gameObject.activeInHierarchy) return;
                // agent.HandleEndEpisodeRewards();
                agent.EndAgentEpisode(); //Call this here?
            }
        }
        
        void resetResettables()
        {
            foreach (var resettable in GetComponentsInChildren<IResettable>())
            {
                resettable.OnReset();
            }
        }
        
        // Resets the agents in the scene
        void resetAgents()
        {
            // Reset every agent in the scene, considers whether or not the agent is falling from the sky
            foreach (AIWAgentController agent in room.Agents)
            {
                var spawnData = room.GetAgentSpawnpoint(agent);
    
                // Actually reset the agent position
                agent.ResetAgent(spawnData);
    
                // Make the agent ragdoll and completely randomized if it's falling from the sky
                if (RoomManager.Instance.GlobalRecordingSettings.FallFromSky) 
                {
                    agent.Helper.DisableAgent();
                    agent.Rig.transform.rotation = Quaternion.Euler(UnityEngine.Random.Range(0, 360), UnityEngine.Random.Range(0, 360), UnityEngine.Random.Range(0, 360));
                    // agent.MainSegment.GetComponent<Rigidbody>().angularVelocity = new Vector3(UnityEngine.Random.Range(0, 360), UnityEngine.Random.Range(0, 360), UnityEngine.Random.Range(0, 360));
                }
            }
        }

        void bypassAgents(bool val)
        {
            foreach (var agent in room.Agents)
            {
                agent.BypassIncomingActions = val;
            }
        }
    }
}
