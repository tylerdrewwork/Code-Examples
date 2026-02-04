# Code-Examples

These are used in various Unity projects I have created or contributed to. 

If you would like to view more examples of my work, please email me at tylerdrew.work@gmail.com

These projects have not been edited in any way other than removing comments and using directives for your viewing clarity.

**Note**: These systems were built under tight time constraints and are messy, but they are robust solutions that met the needs of the project!
<br/>

### **Systems/**
|-- **RoomPhaseController.cs:** 
This subsystem did a lot of heavy lifting for the room's lifecycle. It controls the logic on which state the room is in and which state it will switch to. Another condition was if the current environment was being used for training or recording.

If I did this system again, I would make an explicit state machine that can permit or deny certain transitions. I would also consider using a third party state machine library like Playmaker to handle the room's phase logic visually, but I would need to look at the drawbacks of using a library like that.


|-- **AgentAbilityController.cs:** 
This beast of a script routed the actions from the Unity ML-Agents agentparameters to the abilities on the agent. This is part of the Ability System which massively simplified the process of adding and removing abilities from the agent.
