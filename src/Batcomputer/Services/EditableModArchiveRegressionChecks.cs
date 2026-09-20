namespace Batcomputer;

internal static class EditableModArchiveRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var checks = new List<(bool Passed, string Description)>();
        var root = Path.Combine(Path.GetTempPath(), "BatcomputerEditableArchive_" + Guid.NewGuid().ToString("N"));
        try
        {
            var author = Path.Combine(root, "Author");
            var recipient = Path.Combine(root, "Recipient");
            Directory.CreateDirectory(author);
            var image = Path.Combine(author, "art", "icon.png");
            Directory.CreateDirectory(Path.GetDirectoryName(image)!);
            File.WriteAllBytes(image, [137, 80, 78, 71]);
            var authorSuits = new SuitProjectService(author);
            var suit = new NativeSuitProject { SlotId = "EditableArchiveProof", DisplayName = "Editable archive proof", CoverImagePath = image, PawnTag = "Pawns.Playable.Batman.EditableArchiveProof" };
            var mesh = Path.Combine(authorSuits.ProjectOutputDirectory(suit), "ImportedMeshes", "proof.obj");
            Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
            File.WriteAllText(mesh, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
            File.WriteAllText(Path.ChangeExtension(mesh, ".mtl"), "newmtl Proof\nKd 1 0 0\n");
            suit.CustomStaticMeshes.Add(new() { SourceObjRelativePath = "ImportedMeshes/proof.obj" });
            var textureRoot = Path.Combine(authorSuits.ProjectOutputDirectory(suit), "TextureCache");
            var rawRoot = Path.Combine(textureRoot, "NestedRaw");
            Directory.CreateDirectory(rawRoot);
            File.WriteAllText(Path.Combine(rawRoot, "source.json"), "{}");
            suit.GeneratedTextures.Add(new() { OutputRoot = textureRoot, SourceRawRoot = rawRoot });
            var suitPath = authorSuits.SaveProject(suit);
            var authorMods = new ModProjectService(author);
            var mod = new NativeSuitModProject { ModId = "EditableArchiveProof", DisplayName = "Editable archive proof", CoverImagePath = image };
            mod.PreviousModIds.Add("UnrelatedOldInstall");
            mod.Suits.Add(new ModSuitEntry { SuitId = suit.SlotId, SuitProjectPath = authorMods.MakeRelativeSuitProjectPath(suitPath) });
            var modPath = authorMods.SaveMod(mod);
            var archive = Path.Combine(root, "proof.batcomputer-mod.zip");
            var exported = new EditableModArchiveService(author).Export(modPath, archive);
            var imported = new EditableModArchiveService(recipient).Import(archive);
            var importedMod = new ModProjectService(recipient).LoadMod(imported.ModProjectPath);
            var importedSuitPath = new ModProjectService(recipient).ResolveSuitProjectPath(importedMod!.Suits.Single());
            var importedSuit = new SuitProjectService(recipient).LoadProject(importedSuitPath);
            var recipientMesh = Path.Combine(new SuitProjectService(recipient).ProjectOutputDirectory(importedSuit!), "ImportedMeshes", "proof.obj");
            var archiveStillBlocksCollision = false;
            try { _ = new EditableModArchiveService(recipient).Import(archive); } catch (InvalidDataException) { archiveStillBlocksCollision = true; }
            checks.Add((exported.SourceFiles == 4 && imported.SourceFiles == 4 &&
                          importedMod.ModId == "EditableArchiveProof" && importedSuit is not null &&
                          importedSuit.CoverImagePath.StartsWith(imported.SourceFolder, StringComparison.OrdinalIgnoreCase) &&
                          Path.GetExtension(importedSuit.CoverImagePath) == ".png" && File.Exists(importedSuit.CoverImagePath) && File.Exists(recipientMesh) && File.Exists(Path.ChangeExtension(recipientMesh, ".mtl")) && importedMod.PreviousModIds.Count == 0,
                "editable mod export/import preserves source extensions, relative OBJ/MTL caches and clears previous install identities"));
            var importedTexture = importedSuit!.GeneratedTextures.Single();
            checks.Add((importedTexture.SourceRawRoot == Path.Combine(importedTexture.OutputRoot, "NestedRaw") && File.Exists(Path.Combine(importedTexture.SourceRawRoot, "source.json")),
                "editable sharing rebases nested generated-source directories without flattening their tree"));
            checks.Add((archiveStillBlocksCollision,
                "editable mod import blocks duplicate identities instead of overwriting a workspace"));
            var collisionRoot = Path.Combine(root, "CollisionRecipient");
            var collisionSuits = new SuitProjectService(collisionRoot);
            var existing = collisionSuits.SaveProject(new() { SlotId = "DifferentFile", PawnTag = suit.PawnTag, DisplayName = "Keep me" });
            var original = File.ReadAllText(existing);
            var identityRejected = false;
            try { new EditableModArchiveService(collisionRoot).Import(archive); } catch (InvalidDataException) { identityRejected = true; }
            checks.Add((identityRejected && File.ReadAllText(existing) == original && !File.Exists(collisionSuits.ProjectPathForSlot(suit.SlotId)),
                "editable import rejects a colliding pawn tag despite different suit IDs and preserves the existing project"));
            var malicious = Path.Combine(root, "traversal.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(malicious, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("sources/../escaped.txt").Open())) writer.Write("blocked");
            var rejected = false;
            try { new EditableModArchiveService(Path.Combine(root, "AttackTarget")).Import(malicious); }
            catch (InvalidDataException) { rejected = true; }
            checks.Add((rejected && !Directory.Exists(Path.Combine(root, "AttackTarget")), "unsafe archive paths are rejected before creating import files"));
            var missing = Path.Combine(root, "missing.zip");
            File.Delete(mesh);
            var missingRejected = false;
            try { new EditableModArchiveService(author).Export(modPath, missing); } catch (InvalidDataException) { missingRejected = true; }
            checks.Add((missingRejected && !File.Exists(missing), "missing editable mesh sources fail export instead of silently producing an incomplete archive"));
        }
        catch (Exception ex)
        {
            checks.Add((false, "editable mod archive round trip: " + ex.Message));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
        return checks;
    }
}
