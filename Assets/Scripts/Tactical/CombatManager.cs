using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

public enum CombatState {
    COMBAT_START, COMBAT_END,
    ROUND_START, ROUND_END,
    TURN_START, TURN_END,
    AWAITING_ABILITY_INPUT, AWAITING_CLASH_INPUT,
    RESOLVE_ABILITIES
}

// The combat instance class holds all combat information.
// Why not just in the CombatManager itself? This should make cleanup after combat far easier.
// Instead of having to manually reset all fields in CombatManager, just discard the old CombatInstance and create a new CombatInstance.
public class CombatInstance {
    public CombatState combatState;
    public CombatEventManager combatEvents;

    public Dictionary<CharacterFaction, List<AbstractCharacter>> fighters = new Dictionary<CharacterFaction, List<AbstractCharacter>>{
        {CharacterFaction.PLAYER, new List<AbstractCharacter>()},
        {CharacterFaction.ALLY, new List<AbstractCharacter>()},
        {CharacterFaction.NEUTRAL, new List<AbstractCharacter>()},
        {CharacterFaction.ENEMY, new List<AbstractCharacter>()}
    };

    public ModdablePriorityQueue<AbstractCharacter> turnlist = new ModdablePriorityQueue<AbstractCharacter>();
    public int round;
    public (AbstractCharacter character, int spd) activeChar;

    public CombatInstance(){
        round = 1;
        combatState = CombatState.COMBAT_START;
        combatEvents = new CombatEventManager();
    }

    public AbstractAbility activeAbility;
    public List<Die> activeAbilityDice;
    public List<AbstractCharacter> activeAbilityTargets;
    public AbstractAbility reactAbility;
    public List<Die> reactAbilityDice;

}

public static class CombatManager {
    public static CombatInstance combatData;
    public static CombatEventManager eventManager;

    public static void ChangeCombatState(CombatState newState){
        if (combatData != null && combatData.combatState != newState){
            Debug.Log($"Combat state changing: {combatData.combatState} -> {newState}");
            combatData.combatState = newState;
            ResolveCombatState();
        }
    }

    public static void ResolveCombatState(){
        switch (combatData.combatState){
            case CombatState.COMBAT_START:
                CombatStart();
                break;
            case CombatState.COMBAT_END:
                CombatEnd();
                break;
            case CombatState.ROUND_START:
                RoundStart();
                break;
            case CombatState.ROUND_END:
                RoundEnd();
                break;
            case CombatState.TURN_START:
                TurnStart();
                break;
            case CombatState.TURN_END:
                TurnEnd();
                break;
            case CombatState.AWAITING_ABILITY_INPUT:    // This state doesn't do anything by itself, but allows use of InputAbility while at this stage.
                break;
            case CombatState.AWAITING_CLASH_INPUT:      // This state doesn't do anything by itself, but allows use of InputClashReaction while at this stage.
                break;
            case CombatState.RESOLVE_ABILITIES:         // Triggers after AWAITING_ABILITY_INPUT, or (optionally) AWAITING_CLASH_INPUT.
                break;
            default:
                break;
        }
    }

    private static void CombatStart(){
        eventManager = new CombatEventManager();
        eventManager.BroadcastEvent(new CombatEventCombatStart());
        ChangeCombatState(CombatState.ROUND_START);
    }

    private static void CombatEnd(){
        eventManager.BroadcastEvent(new CombatEventCombatEnd());
        combatData = null;        // Clean up by discarding both the combat data instance and combat event info instance.
        eventManager = null;
    }

    private static void RoundStart(){
        foreach (CharacterFaction faction in combatData.fighters.Keys){
            foreach (AbstractCharacter character in combatData.fighters[faction]){
                for (int i = 0; i < character.actionsPerTurn; i++){
                    combatData.turnlist.AddToQueue(character, Random.Range(character.minSpd, character.maxSpd));
                }
            }
        }
        eventManager.BroadcastEvent(new CombatEventRoundStart(combatData.round));
        ChangeCombatState(CombatState.TURN_START);
    }

    private static void RoundEnd(){
        eventManager.BroadcastEvent(new CombatEventRoundEnd(combatData.round));
        combatData.round += 1;
        ChangeCombatState(CombatState.ROUND_START);
    }

    private static void TurnStart(){
        combatData.activeChar = combatData.turnlist.PopNextItem();
        eventManager.BroadcastEvent(new CombatEventTurnStart(combatData.activeChar.character, combatData.activeChar.spd));
        ChangeCombatState(CombatState.AWAITING_ABILITY_INPUT);
    }

    private static void TurnEnd(){
        eventManager.BroadcastEvent(new CombatEventTurnEnd(combatData.activeChar.character, combatData.activeChar.spd));
        if (combatData.turnlist.GetNextItem() == (null, 0)){
            combatData.activeChar = combatData.turnlist.PopNextItem();
            ChangeCombatState(CombatState.ROUND_END);
        } else {
            ChangeCombatState(CombatState.TURN_START);
        }
    }

    // Input abilities which are unit-targeted.
    public static void InputAbility(AbstractAbility ability, List<AbstractCharacter> targets){
        // Don't do anything if not in AWAITING_ABILITY_INPUT stage, or if no targets were selected.
        if (combatData.combatState != CombatState.AWAITING_ABILITY_INPUT || targets.Count == 0){
            return;
        }
        combatData.activeAbility = ability;
        combatData.activeAbilityDice = ability.BASE_DICE;
        combatData.activeAbilityTargets = targets;

        eventManager.BroadcastEvent(new CombatEventAbilityActivated(combatData.activeChar.character, ability, ability.BASE_DICE, targets));
        if (ability.TYPE == AbilityType.ATTACK &&
            !ability.HasTag(AbilityTag.AOE) && !ability.HasTag(AbilityTag.DEVIOUS) &&
            combatData.turnlist.ContainsItem(targets[0]) &&
            CheckForEligibleReactions().Count > 0) {
            // If the ability in question is a single-target non-DEVIOUS attack, the defender has a remaining action, and the defender has eligible reactions, change to AWAITING_CLASH_INPUT.
            ChangeCombatState(CombatState.AWAITING_CLASH_INPUT);
        } else {
            ChangeCombatState(CombatState.RESOLVE_ABILITIES);
        }
    }

    // Input abilities which are lane-targeted.
    public static void InputAbility(AbstractAbility ability, List<int> lanes){
        // Don't do anything if not in AWAITING_ABILITY_INPUT stage, or if no targets were selected.
        if (combatData.combatState != CombatState.AWAITING_ABILITY_INPUT){
            return;
        }
    }
    
    private static List<AbstractAbility> CheckForEligibleReactions(){
        int atkLane = combatData.activeChar.character.curPos;
        AbstractCharacter defender = combatData.activeAbilityTargets[0];
        int defLane = defender.curPos;
        List<AbstractAbility> availableReactionAbilties = new List<AbstractAbility>();
        foreach (AbstractAbility ability in defender.abilities){ 
            if (!ability.isAvailable || ability.HasTag(AbilityTag.CANNOT_REACT)){
                continue;
            }
            // Available reactions are always eligible for reactions.
            if (ability.TYPE == AbilityType.REACTION){
                availableReactionAbilties.Add(ability);
            }
            // Available attacks are eligible *if* the attacker is in range of the defender's attack.
            if (ability.TYPE == AbilityType.ATTACK){
                int range = Math.Abs(atkLane - defLane);
                if (range >= ability.MIN_RANGE && range <= ability.MAX_RANGE) availableReactionAbilties.Add(ability);
            }
        }
        return availableReactionAbilties;
    }
}