using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FistVR;
using FistVR.Ugc;
using UnityEngine;
using UnityEngine.SceneManagement;
using Valve.Newtonsoft.Json;

/// <summary>
/// Bounded live-game vault round trip for the original, already-spawned handgun.
/// Call only after NativeGameplayProbe has observed its normal open-hand release.
/// This fixture does not change Vault eligibility: it verifies CanScanPhysicalObject
/// and then calls ScanObjectToVaultFile on the same real object.
///
/// Persistence follows the current VaultFile IUgcItem path: UgcManager.SaveItem
/// writes a unique .VaultFile.json under the explicitly supplied workspace build
/// directory, then the production JsonConvert.PopulateObject and serializer
/// settings reload it into a fresh VaultFile. It intentionally does not use the
/// legacy CynJson Favorites helper, which targets the user's H3VR data directory
/// and is not the current VaultFile serializer.
///
/// The loaded object is respawned by VaultSystem.SpawnVaultFile with
/// clearScene=false. The callback, exact game component type, ObjectID, chamber
/// round classes/live state, and all game-reported flags are checked.
/// </summary>
public static class NativeGameplayVaultFixture {
    const string RequiredBuildPathSuffix = "/platform/gameplay-probe/build/vault-fixture";

    public static IEnumerator RunAfterOpenHandRelease(FVRFireArm firearm, string fixtureDirectory) {
        if (firearm == null)
            throw new ArgumentNullException("firearm");
        if (!(firearm is Handgun))
            throw new InvalidOperationException(
                "The Vault round-trip fixture currently validates a representative Handgun only");

        string root = ValidateFixtureDirectory(fixtureDirectory);
        if (firearm.IsHeld || firearm.m_hand != null || firearm.QuickbeltSlot != null ||
            firearm.RootRigidbody == null || firearm.RootRigidbody.transform.parent != null)
            throw new InvalidOperationException(
                "Vault fixture requires the real firearm to be released and unparented");
        if (!VaultSystem.CanScanPhysicalObject(firearm))
            throw new InvalidOperationException(
                "The original VaultSystem reports the released firearm is not save-loadable");

        // Prepare one valid round through the existing original-game acceptance
        // helper. This makes the chamber round state non-empty for round-trip
        // verification; it does not alter the physical object's Vault eligibility.
        NativeGameplayLoadFixture.PrepareOneShot(firearm);
        Dictionary<string, string> expectedFlags = CopyFlags(firearm.GetFlagDic());
        List<FireArmRoundClass> expectedRounds = firearm.GetChamberRoundList();
        if (expectedRounds == null || expectedRounds.Count == 0)
            throw new InvalidOperationException(
                "Original firearm API did not expose a loaded chamber round");

        VaultFile scanned = new VaultFile();
        scanned.Type = VaultFileType.SingleObject;
        scanned.FileName = "NativeGameplayVaultFixture_" + Guid.NewGuid().ToString("N");
        scanned.Creator = "NativeGameplayProbe";
        scanned.SceneName = SceneManager.GetActiveScene().name;
        scanned.UgcIsWritable = true;
        scanned.UgcFilePath = Path.Combine(root, "object_" + scanned.FileName + ".VaultFile.json");

        if (!VaultSystem.ScanObjectToVaultFile(scanned, firearm))
            throw new InvalidOperationException(
                "Original VaultSystem.ScanObjectToVaultFile rejected the released firearm");
        ValidateScannedObject(scanned, firearm, expectedFlags, expectedRounds);

        // SaveItem is the game UGC file API. The path is new and allowlisted above;
        // this never inserts the test file into a user's Vault tree.
        UgcManager.SaveItem(scanned);
        if (!File.Exists(scanned.UgcFilePath))
            throw new IOException(
                "UgcManager.SaveItem returned without creating the isolated VaultFile");

        VaultFile loaded = new VaultFile();
        loaded.UgcFilePath = scanned.UgcFilePath;
        JsonConvert.PopulateObject(File.ReadAllText(loaded.UgcFilePath), loaded,
                                   UgcManager.GetJsonSerializerSettings());
        ValidateLoadedFile(loaded, scanned, firearm, expectedFlags, expectedRounds);

        List<FVRPhysicalObject> callbackObjects = null;
        int callbackCount = 0;
        VaultSystem.ReturnObjectListDelegate callback = delegate(List<FVRPhysicalObject> objects) {
            callbackCount++;
            callbackObjects = objects;
        };

        string error;
        bool accepted = VaultSystem.SpawnVaultFile(
            loaded, firearm.transform, spawnRelativeTo: true, decodeAsLoadout: false,
            clearScene: false, ErrorMessage: out error, spawnOffset: Vector3.right * 0.6f,
            del: callback, displayFileLoad: false, onlySpawnSpecificObjectIndex: 0);
        if (!accepted)
            throw new InvalidOperationException(
                "Original VaultSystem.SpawnVaultFile rejected the round-tripped file: " + error);

        float deadline = Time.realtimeSinceStartup + 20f;
        while (callbackCount == 0 && Time.realtimeSinceStartup < deadline)
            yield return null;
        if (callbackCount != 1 || callbackObjects == null || callbackObjects.Count != 1)
            throw new InvalidOperationException(
                "Original Vault spawn callback did not return exactly one root object; callbacks=" +
                callbackCount +
                ", objects=" + (callbackObjects == null ? -1 : callbackObjects.Count));

        FVRPhysicalObject spawned = callbackObjects[0];
        if (spawned == null)
            throw new InvalidOperationException(
                "Original Vault spawn callback returned a null root object");
        FVRFireArm spawnedFirearm = spawned as FVRFireArm;
        if (spawnedFirearm == null || spawned.GetType() != firearm.GetType())
            throw new InvalidOperationException("Vault spawn changed game type: expected " +
                                                firearm.GetType().FullName + ", got " +
                                                spawned.GetType().FullName);
        if (spawned.ObjectWrapper == null ||
            spawned.ObjectWrapper.ItemID != firearm.ObjectWrapper.ItemID)
            throw new InvalidOperationException(
                "Vault spawn changed the original firearm ObjectID");

        AssertFlagsEqual(expectedFlags, spawned.GetFlagDic(), "respawned firearm");
        AssertRoundListsEqual(expectedRounds, spawnedFirearm.GetChamberRoundList(),
                              "respawned firearm");
        AssertLoadedLiveChamber(spawnedFirearm, expectedRounds);

        string sha256 = Sha256File(scanned.UgcFilePath);
        Debug.Log("NATIVE_GAMEPLAY_VAULT_FIXTURE_PASS path=" + scanned.UgcFilePath +
                  " sha256=" + sha256 + " type=" + spawned.GetType().FullName +
                  " objectId=" + spawned.ObjectWrapper.ItemID +
                  " chamberRounds=" + expectedRounds.Count + " flags=" + expectedFlags.Count +
                  " callbackCount=" + callbackCount + " clearScene=false");
    }

    static string ValidateFixtureDirectory(string directory) {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
            throw new ArgumentException("An absolute isolated workspace build path is required",
                                        "directory");
        string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,
                                                          Path.AltDirectorySeparatorChar);
        string normalized = full.Replace('\\', '/');
        if (!normalized.EndsWith(RequiredBuildPathSuffix, StringComparison.Ordinal))
            throw new ArgumentException("Vault fixture output must be exactly under " +
                                            RequiredBuildPathSuffix,
                                        "directory");
        if (normalized.IndexOf("/My Games/H3VR/", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("/Vault/", StringComparison.OrdinalIgnoreCase) >= 0)
            throw new ArgumentException("Vault fixture output cannot target a user Vault directory",
                                        "directory");
        Directory.CreateDirectory(full);
        return full;
    }

    static void ValidateScannedObject(VaultFile file, FVRFireArm firearm,
                                      Dictionary<string, string> expectedFlags,
                                      List<FireArmRoundClass> expectedRounds) {
        if (file.Objects == null || file.Objects.Count != 1 || file.Objects[0].Elements == null ||
            file.Objects[0].Elements.Count != 1)
            throw new InvalidOperationException(
                "Original Vault scan did not encode exactly one root firearm element");
        VaultElement element = file.Objects[0].Elements[0];
        if (element.Type != "firearm" || element.ObjectID != firearm.ObjectWrapper.ItemID)
            throw new InvalidOperationException(
                "Original Vault scan encoded the wrong object type or ObjectID");
        AssertFlagsEqual(expectedFlags, element.Flags == null ? null : element.Flags.dictionary,
                         "scanned VaultElement");
        AssertRoundListsEqual(expectedRounds, element.LoadedRoundsInChambers,
                              "scanned VaultElement");
    }

    static void ValidateLoadedFile(VaultFile loaded, VaultFile original, FVRFireArm firearm,
                                   Dictionary<string, string> expectedFlags,
                                   List<FireArmRoundClass> expectedRounds) {
        if (loaded.Type != VaultFileType.SingleObject || loaded.FileName != original.FileName ||
            loaded.Creator != original.Creator || loaded.Objects == null ||
            loaded.Objects.Count != 1 || loaded.Objects[0].Elements == null ||
            loaded.Objects[0].Elements.Count != 1)
            throw new InvalidOperationException(
                "UGC JSON reload lost required VaultFile metadata or object topology");
        VaultElement element = loaded.Objects[0].Elements[0];
        if (element.Type != "firearm" || element.ObjectID != firearm.ObjectWrapper.ItemID)
            throw new InvalidOperationException("UGC JSON reload changed firearm type or ObjectID");
        AssertFlagsEqual(expectedFlags, element.Flags == null ? null : element.Flags.dictionary,
                         "reloaded VaultElement");
        AssertRoundListsEqual(expectedRounds, element.LoadedRoundsInChambers,
                              "reloaded VaultElement");
    }

    static void AssertLoadedLiveChamber(FVRFireArm firearm, List<FireArmRoundClass> expected) {
        Handgun handgun = firearm as Handgun;
        if (handgun == null || handgun.Chamber == null || !handgun.Chamber.IsFull ||
            handgun.Chamber.IsSpent || handgun.Chamber.GetRound() == null)
            throw new InvalidOperationException(
                "Vault respawn did not restore a live round in the representative handgun chamber");
        if (expected.Count != 1 || handgun.Chamber.GetRound().RoundClass != expected[0] ||
            handgun.Chamber.GetRound().RoundType != handgun.RoundType)
            throw new InvalidOperationException(
                "Vault respawn restored a different chamber round class/type");
    }

    static Dictionary<string, string> CopyFlags(Dictionary<string, string> flags) {
        return flags == null ? new Dictionary<string, string>()
                             : new Dictionary<string, string>(flags);
    }

    static void AssertFlagsEqual(Dictionary<string, string> expected,
                                 Dictionary<string, string> actual, string stage) {
        if (expected == null)
            expected = new Dictionary<string, string>();
        if (actual == null)
            actual = new Dictionary<string, string>();
        if (expected.Count != actual.Count)
            throw new InvalidOperationException(stage + " flag count mismatch: expected " +
                                                expected.Count + ", got " + actual.Count);
        foreach (KeyValuePair<string, string> item in expected) {
            string value;
            if (!actual.TryGetValue(item.Key, out value) || value != item.Value)
                throw new InvalidOperationException(stage + " flag mismatch for '" + item.Key +
                                                    "'");
        }
    }

    static void AssertRoundListsEqual(List<FireArmRoundClass> expected,
                                      List<FireArmRoundClass> actual, string stage) {
        if (expected == null)
            expected = new List<FireArmRoundClass>();
        if (actual == null)
            actual = new List<FireArmRoundClass>();
        if (expected.Count != actual.Count)
            throw new InvalidOperationException(stage + " chamber round count mismatch: expected " +
                                                expected.Count + ", got " + actual.Count);
        for (int i = 0; i < expected.Count; i++)
            if (!EqualityComparer<FireArmRoundClass>.Default.Equals(expected[i], actual[i]))
                throw new InvalidOperationException(stage + " chamber round mismatch at index " +
                                                    i);
    }

    static string Sha256File(string path) {
        using (SHA256 sha = SHA256.Create()) using (FileStream stream = File.OpenRead(path)) {
            byte[] digest = sha.ComputeHash(stream);
            StringBuilder text = new StringBuilder(digest.Length * 2);
            foreach (byte value in digest)
                text.Append(value.ToString("x2"));
            return text.ToString();
        }
    }
}
