using System;
using System.Collections.Generic;

using FistVR;

/// <summary>
/// Prepares a one-round acceptance fixture using original save/load APIs.
/// Firing must still happen through the held firearm's normal trigger input.
/// </summary>
public static class NativeGameplayLoadFixture {
    public static FVRFireArmChamber PrepareOneShot(FVRFireArm firearm) {
        if (firearm == null)
            throw new ArgumentNullException("firearm");

        FireArmRoundClass roundClass = SelectRoundClass(firearm);

        Handgun handgun = firearm as Handgun;
        if (handgun != null) {
            handgun.SetLoadedChambers(new List<FireArmRoundClass> { roundClass });
            Dictionary<string, string> flags = handgun.GetFlagDic();
            flags["HammerState"] = "Cocked";
            flags["SafetyState"] = "Off";
            handgun.ConfigureFromFlagDic(flags);
            if (handgun.HasSafety && handgun.IsSafetyEngaged)
                handgun.SetSafetyState(false);
            return VerifyLoaded(handgun, handgun.Chamber, roundClass);
        }

        ClosedBoltWeapon closedBolt = firearm as ClosedBoltWeapon;
        if (closedBolt != null) {
            if (closedBolt.FireSelector_Modes == null)
                throw new InvalidOperationException(
                    "Closed-bolt firearm has no original selector modes");
            int single = Array.FindIndex(closedBolt.FireSelector_Modes,
                                         mode => mode.ModeType ==
                                                 ClosedBoltWeapon.FireSelectorModeType.Single);
            if (single < 0)
                throw new InvalidOperationException(
                    "Closed-bolt fixture requires an original single-shot selector mode");

            Dictionary<string, string> flags = closedBolt.GetFlagDic();
            flags["FireSelectorState"] = single.ToString();
            flags["HammerState"] = "Cocked";
            closedBolt.ConfigureFromFlagDic(flags);
            if (closedBolt.IsWeaponOnSafe())
                throw new InvalidOperationException(
                    "Original closed-bolt selector remained on safe");
            closedBolt.SetLoadedChambers(new List<FireArmRoundClass> { roundClass });
            return VerifyLoaded(closedBolt, closedBolt.Chamber, roundClass);
        }

        Revolver revolver = firearm as Revolver;
        if (revolver != null) {
            if (!revolver.IsDoubleActionTrigger)
                throw new InvalidOperationException(
                    "Single-action revolver requires a normal manual hammer interaction before " +
                    "trigger input");
            if (revolver.Chambers == null || revolver.Chambers.Length == 0 ||
                revolver.Cylinder == null)
                throw new InvalidOperationException(
                    "Revolver fixture has no original cylinder chambers");

            // Revolver trigger input advances the cylinder before striking. Load
            // exactly one original round and point the current chamber so that
            // the next original trigger cycle presents chamber zero.
            revolver.SetLoadedChambers(new List<FireArmRoundClass> { roundClass });
            int count = revolver.Cylinder.numChambers;
            if (count <= 0 || count != revolver.Chambers.Length)
                throw new InvalidOperationException(
                    "Revolver cylinder/chamber topology is invalid");
            int step = revolver.IsCylinderRotClockwise ? 1 : -1;
            int current = -revolver.ChamberOffset - step;
            current = ((current % count) + count) % count;
            revolver.CurChamber = current;
            return VerifyLoaded(revolver, revolver.Chambers[0], roundClass);
        }

        BoltActionRifle boltAction = firearm as BoltActionRifle;
        if (boltAction != null) {
            if (boltAction.BoltHandle == null ||
                boltAction.CurBoltHandleState !=
                    BoltActionRifle_Handle.BoltActionHandleState.Forward ||
                boltAction.BoltHandle.HandleRot != BoltActionRifle_Handle.BoltActionHandleRot.Down)
                throw new InvalidOperationException(
                    "Bolt-action fixture requires its original bolt forward and handle down; " +
                    "cycle it normally first");

            if (boltAction.FireSelector_Modes == null)
                throw new InvalidOperationException(
                    "Bolt-action firearm has no original selector modes");
            int single = Array.FindIndex(boltAction.FireSelector_Modes,
                                         mode => mode.ModeType ==
                                                 BoltActionRifle.FireSelectorModeType.Single);
            if (single < 0)
                throw new InvalidOperationException(
                    "Bolt-action fixture has no original single-shot selector mode");
            Dictionary<string, string> flags = boltAction.GetFlagDic();
            flags["FireSelectorState"] = single.ToString();
            flags["HammerState"] = "Cocked";
            boltAction.ConfigureFromFlagDic(flags);
            boltAction.SetLoadedChambers(new List<FireArmRoundClass> { roundClass });
            return VerifyLoaded(boltAction, boltAction.Chamber, roundClass);
        }

        TubeFedShotgun shotgun = firearm as TubeFedShotgun;
        if (shotgun != null) {
            if (shotgun.Bolt == null || shotgun.Bolt.CurPos != TubeFedShotgunBolt.BoltPos.Forward)
                throw new InvalidOperationException(
                    "Shotgun fixture requires its original bolt forward; cycle it normally first");

            // Prefer the original pump mode when the firearm exposes its normal
            // mode switch, so holding a synthetic trigger cannot auto-cycle into
            // another round during the bounded one-shot observation.
            if (shotgun.Mode == TubeFedShotgun.ShotgunMode.Automatic && shotgun.CanModeSwitch &&
                !shotgun.IsHammerCocked)
                shotgun.ToggleMode();

            Dictionary<string, string> flags = shotgun.GetFlagDic();
            flags["HammerState"] = "Cocked";
            flags["SafetyState"] = "Off";
            shotgun.ConfigureFromFlagDic(flags);
            shotgun.SetLoadedChambers(new List<FireArmRoundClass> { roundClass });
            if (shotgun.HasSafety && shotgun.IsSafetyEngaged)
                shotgun.ToggleSafety();
            return VerifyLoaded(shotgun, shotgun.Chamber, roundClass);
        }

        if (firearm is OpenBoltReceiver)
            throw new InvalidOperationException(
                "Open-bolt firing requires an original magazine feed and physical bolt cycle; " +
                "this chamber-only fixture cannot safely prepare it");

        throw new InvalidOperationException("Unsupported original firearm fixture type: " +
                                            firearm.GetType().FullName);
    }

    static FireArmRoundClass SelectRoundClass(FVRFireArm firearm) {
        if (firearm is TubeFedShotgun) {
            FireArmRoundClass[] shotgunClasses = { FireArmRoundClass.BuckShotNo4,
                                                   FireArmRoundClass.BuckShot00,
                                                   FireArmRoundClass.BuckShot000,
                                                   FireArmRoundClass.Slug };
            foreach (FireArmRoundClass candidate in shotgunClasses)
                if (AM.DoesClassExistForType(candidate, firearm.RoundType))
                    return candidate;
        } else if (AM.DoesClassExistForType(FireArmRoundClass.FMJ, firearm.RoundType)) {
            return FireArmRoundClass.FMJ;
        }

        FireArmRoundClass defaultClass = AM.GetDefaultRoundClass(firearm.RoundType);
        if (!AM.DoesClassExistForType(defaultClass, firearm.RoundType))
            throw new InvalidOperationException(
                "No original ammunition class exists for round type " + firearm.RoundType);
        return defaultClass;
    }

    static FVRFireArmChamber VerifyLoaded(FVRFireArm firearm, FVRFireArmChamber chamber,
                                          FireArmRoundClass expectedClass) {
        if (chamber == null || !chamber.IsFull || chamber.IsSpent || chamber.GetRound() == null ||
            chamber.GetRound().RoundClass != expectedClass ||
            chamber.GetRound().RoundType != firearm.RoundType)
            throw new InvalidOperationException(
                "Original firearm load API did not produce the requested live round");
        return chamber;
    }
}
