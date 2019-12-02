﻿using Celeste;
using Celeste.Mod;
using ExtendedVariants.Entities;
using ExtendedVariants.Module;
using Microsoft.Xna.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod.Cil;
using System;
using System.Collections;
using System.Collections.Generic;

namespace ExtendedVariants.Variants {
    public class BadelineChasersEverywhere : AbstractExtendedVariant {
        public override int GetDefaultValue() {
            return 0;
        }

        public override int GetValue() {
            return Settings.BadelineChasersEverywhere ? 1 : 0;
        }

        public override void SetValue(int value) {
            Settings.BadelineChasersEverywhere = (value != 0);
        }

        public override void Load() {
            IL.Celeste.BadelineOldsite.ctor_Vector2_int += modBadelineOldsiteConstructor;
            On.Celeste.Level.LoadLevel += modLoadLevel;
            On.Celeste.Level.TransitionRoutine += modTransitionRoutine;
            IL.Celeste.BadelineOldsite.Added += modBadelineOldsiteAdded;
            IL.Celeste.BadelineOldsite.CanChangeMusic += modBadelineOldsiteCanChangeMusic;
            On.Celeste.BadelineOldsite.IsChaseEnd += modBadelineOldsiteIsChaseEnd;
            IL.Celeste.Player.UpdateChaserStates += modUpdateChaserStates;
        }

        public override void Unload() {
            IL.Celeste.BadelineOldsite.ctor_Vector2_int -= modBadelineOldsiteConstructor;
            On.Celeste.Level.LoadLevel -= modLoadLevel;
            On.Celeste.Level.TransitionRoutine -= modTransitionRoutine;
            IL.Celeste.BadelineOldsite.Added -= modBadelineOldsiteAdded;
            IL.Celeste.BadelineOldsite.CanChangeMusic -= modBadelineOldsiteCanChangeMusic;
            On.Celeste.BadelineOldsite.IsChaseEnd -= modBadelineOldsiteIsChaseEnd;
            IL.Celeste.Player.UpdateChaserStates -= modUpdateChaserStates;
        }

        private void modBadelineOldsiteConstructor(ILContext il) {
            ILCursor cursor = new ILCursor(il);

            // go everywhere where the 1.55 second delay is defined
            while (cursor.TryGotoNext(MoveType.After, instr => instr.MatchLdcR4(1.55f))) {
                Logger.Log("ExtendedVariantMode/BadelineChasersEverywhere", $"Modding Badeline lag at {cursor.Index} in CIL code for BadelineOldsite constructor");

                // and substitute it with our own value
                cursor.Emit(OpCodes.Pop);
                cursor.EmitDelegate<Func<float>>(determineBadelineLag);
            }

            cursor.Index = 0;

            // go everywhere where the 0.4 second delay between Badelines is defined
            while (cursor.TryGotoNext(MoveType.After, instr => instr.MatchLdcR4(0.4f))) {
                Logger.Log("ExtendedVariantMode/BadelineChasersEverywhere", $"Modding delay between Badelines at {cursor.Index} in CIL code for BadelineOldsite constructor");

                // and substitute it with our own value
                cursor.Emit(OpCodes.Pop);
                cursor.EmitDelegate<Func<float>>(determineDelayBetweenBadelines);
            }
        }

        private float determineBadelineLag() {
            return ExtendedVariantsModule.ShouldIgnoreCustomDelaySettings() || Settings.BadelineLag == 0 ? 1.55f : Settings.BadelineLag / 10f;
        }

        private float determineDelayBetweenBadelines() {
            return Settings.DelayBetweenBadelines / 10f;
        }

        /// <summary>
        /// Wraps the LoadLevel method in order to add Badeline chasers when needed.
        /// </summary>
        /// <param name="orig">The base method</param>
        /// <param name="self">The level entity</param>
        /// <param name="playerIntro">The type of player intro</param>
        /// <param name="isFromLoader">unused</param>
        private void modLoadLevel(On.Celeste.Level.orig_LoadLevel orig, Level self, Player.IntroTypes playerIntro, bool isFromLoader) {
            orig(self, playerIntro, isFromLoader);

            // this method takes care of every situation except transitions, we let this one to TransitionRoutine
            if (Settings.BadelineChasersEverywhere && playerIntro != Player.IntroTypes.Transition) {
                // set this to avoid the player being instakilled during the level intro animation
                Player player = self.Tracker.GetEntity<Player>();
                if (player != null) player.JustRespawned = true;
            }

            if((Settings.BadelineChasersEverywhere || Settings.AffectExistingChasers) && playerIntro != Player.IntroTypes.Transition) {
                injectBadelineChasers(self);
            }
        }

        /// <summary>
        /// Wraps the TransitionRoutine in Level, in order to add Badeline chasers when needed.
        /// This is not done in LoadLevel, since this one will wait for the transition to be done, so that the entities from the previous screen are unloaded.
        /// </summary>
        /// <param name="orig">The base method</param>
        /// <param name="self">The level entity</param>
        /// <param name="next">unused</param>
        /// <param name="direction">unused</param>
        /// <returns></returns>
        private IEnumerator modTransitionRoutine(On.Celeste.Level.orig_TransitionRoutine orig, Level self, LevelData next, Vector2 direction) {
            // just make sure the whole transition routine is over
            IEnumerator origEnum = orig(self, next, direction);
            while (origEnum.MoveNext()) {
                yield return origEnum.Current;
            }

            // then decide whether to add Badeline or not
            injectBadelineChasers(self);

            yield break;
        }

        /// <summary>
        /// Mods the Added method in BadelineOldsite, to make it not kill chasers on screens they are not supposed to be.
        /// </summary>
        /// <param name="il">Object allowing IL modding</param>
        private void modBadelineOldsiteAdded(ILContext il) {
            ILCursor cursor = new ILCursor(il);

            // go right after the equality check that compares the level set name with "Celeste"
            while (cursor.TryGotoNext(MoveType.After, instr => instr.OpCode == OpCodes.Call && ((MethodReference)instr.Operand).Name.Contains("op_Equality"))) {
                Logger.Log("ExtendedVariantMode/BadelineChasersEverywhere", $"Modding vanilla level check at index {cursor.Index} in the Added method from BadelineOldsite");

                // mod the result of that check to prevent the chasers we will spawn from... committing suicide
                cursor.Emit(OpCodes.Ldarg_1);
                cursor.EmitDelegate<Func<bool, Scene, bool>>(modVanillaBehaviorCheckForChasers);
            }
        }

        /// <summary>
        /// Mods the CanChangeMusic method in BadelineOldsite, so that forcibly added chasers do not change the level music.
        /// </summary>
        /// <param name="il">Object allowing IL modding</param>
        private void modBadelineOldsiteCanChangeMusic(ILContext il) {
            ILCursor cursor = new ILCursor(il);

            // go right after the equality check that compares the level set name with "Celeste"
            while (cursor.TryGotoNext(MoveType.After, instr => instr.OpCode == OpCodes.Call && ((MethodReference)instr.Operand).Name.Contains("op_Equality"))) {
                Logger.Log("ExtendedVariantMode/BadelineChasersEverywhere", $"Modding vanilla level check at index {cursor.Index} in the CanChangeMusic method from BadelineOldsite");

                // mod the result of that check to always use modded value, even in vanilla levels
                cursor.EmitDelegate<Func<bool, bool>>(modVanillaBehaviorCheckForMusic);
            }
        }

        private void injectBadelineChasers(Level level) {
            bool hasChasersInBaseLevel = level.Tracker.CountEntities<BadelineOldsite>() != 0;

            if (Settings.BadelineChasersEverywhere) {
                Player player = level.Tracker.GetEntity<Player>();

                // check if the base level already has chasers
                if (player != null && !hasChasersInBaseLevel) {
                    // add a Badeline chaser where the player is, and tell it not to change the music to the chase music
                    for (int i = 0; i < Settings.ChaserCount; i++) {
                        level.Add(new AutoDestroyingBadelineOldsite(generateBadelineEntityData(level, i), player.Position, i));
                    }

                    level.Entities.UpdateLists();
                }
            }

            // plz disregard the settings and don't touch the chasers if in Badeline Intro cutscene
            // because the chaser triggers the cutscene, so having 10 chasers triggers 10 instances of the cutscene at the same time (a)
            if(Settings.AffectExistingChasers && hasChasersInBaseLevel && notInBadelineIntroCutscene(level)) {
                List<Entity> chasers = level.Tracker.GetEntities<BadelineOldsite>();
                if (chasers.Count > Settings.ChaserCount) {
                    // for example, if there are 6 chasers and we want 3, we will ask chasers 4-6 to commit suicide
                    for(int i = chasers.Count - 1; i >= Settings.ChaserCount; i--) {
                        chasers[i].RemoveSelf();
                    }
                } else if(chasers.Count < Settings.ChaserCount) {
                    // for example, if we have 2 chasers and we want 6, we will duplicate both chasers twice
                    for(int i = chasers.Count; i < Settings.ChaserCount; i++) {
                        int baseChaser = i % chasers.Count;
                        level.Add(new AutoDestroyingBadelineOldsite(generateBadelineEntityData(level, i), chasers[baseChaser].Position, i));
                    }
                }

                level.Entities.UpdateLists();
            }
        }

        private bool notInBadelineIntroCutscene(Level level) {
            return (level.Session.Area.GetSID() != "Celeste/2-OldSite" || level.Session.Level != "3" || level.Session.Area.Mode != AreaMode.Normal);
        }

        private EntityData generateBadelineEntityData(Level level, int badelineNumber) {
            EntityData entityData = ExtendedVariantsModule.GenerateBasicEntityData(level, badelineNumber);
            entityData.Values["canChangeMusic"] = false;
            return entityData;
        }

        private bool modVanillaBehaviorCheckForMusic(bool shouldUseVanilla) {
            // we can use the "flag-based behavior" on all A-sides
            if (Engine.Scene.GetType() == typeof(Level) && (Engine.Scene as Level).Session.Area.Mode == AreaMode.Normal) {
                return false;
            }
            // fall back to standard Everest behavior everywhere else: vanilla will not trigger chase music, and Everest will be flag-based
            return shouldUseVanilla;
        }

        private bool modVanillaBehaviorCheckForChasers(bool shouldUseVanilla, Scene scene) {
            Session session = (scene as Level).Session;

            if (Settings.BadelineChasersEverywhere && 
                // don't use vanilla behaviour when that would lead the chasers to commit suicide
                (!session.GetLevelFlag("3") || session.GetLevelFlag("11") || 
                // don't use vanilla behaviour when that would trigger the Badeline intro cutscene, except (of course) on Old Site
                (session.Area.GetSID() != "Celeste/2-OldSite" && session.Level == "3" && session.Area.Mode == AreaMode.Normal))) {
                return false;
            }
            return shouldUseVanilla;
        }

        private bool modBadelineOldsiteIsChaseEnd(On.Celeste.BadelineOldsite.orig_IsChaseEnd orig, BadelineOldsite self, bool value) {
            Session session = self.SceneAs<Level>().Session;
            if (session.Area.GetLevelSet() == "Celeste" && session.Area.GetSID() != "Celeste/2-OldSite") {
                // there is no chase end outside Old Site in the vanilla game.
                return false;
            }
            return orig(self, value);
        }

        /// <summary>
        /// Mods the UpdateChaserStates to tell it to save a bit more history of chaser states, so that we can spawn more chasers.
        /// </summary>
        /// <param name="il">Object allowing IL modding</param>
        private void modUpdateChaserStates(ILContext il) {
            ILCursor cursor = new ILCursor(il);

            // go where the "4" is
            while (cursor.TryGotoNext(MoveType.Before, instr => instr.MatchLdcR4(4f))) {
                Logger.Log("ExtendedVariantMode/BadelineChasersEverywhere", $"Modding constant at {cursor.Index} in the UpdateChaserStates method to allow more chasers to spawn");

                // and replace it with a 10f to have better support for custom settings (f.e. 10 chasers 1 second apart from each other).
                cursor.Next.Operand = 10f;
            }
        }

    }
}
