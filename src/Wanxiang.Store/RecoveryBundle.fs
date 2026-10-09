namespace Wanxiang.Store

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes

/// 修复证据包（算法 B：Backup Before Repair）。
/// 任何破坏性修复（截尾/删除）之前，必须先把受影响文件完整复制到隔离目录，
/// 计算并核对 SHA-256，写入 manifest 与 diagnosis；只有证据包建立成功才允许执行修复。
/// 证据包目录布局：
///   data/recovery/<utc>/manifest.json
///   data/recovery/<utc>/original/<file>...
///   data/recovery/<utc>/diagnosis.json
module RecoveryBundle =

    let private recoveryRoot (dataDir: string) = Path.Combine(dataDir, "recovery")

    let private sha256OfFile (path: string) : string =
        use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        use sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        let buf = Array.zeroCreate<byte> (64 * 1024)
        let rec loop () =
            let n = fs.Read(buf, 0, buf.Length)
            if n > 0 then
                sha.AppendData(buf, 0, n)
                loop ()
        loop ()
        Convert.ToHexStringLower(sha.GetHashAndReset())

    /// 创建证据包。`plan.affectedFiles` 是修复会修改/删除的全部文件。
    /// 返回证据包目录完整路径；任一复制或哈希核对失败即返回 Error（不部分成功）。
    let create (dataDir: string) (plan: RepairPlan) : Result<string, string> =
        try
            let stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss'Z'")
            let dir = Path.Combine(recoveryRoot dataDir, stamp)
            let originalDir = Path.Combine(dir, "original")
            Directory.CreateDirectory originalDir |> ignore
            let manifest = JsonObject()
            manifest["applicationVersion"] <- "wanxiang"
            // P-002：格式唯一权威 = CommitCodec + Constants.FormatVersion（Ledger.Record 重复定义已删除）
            manifest["formatVersion"] <- Wanxiang.Core.Constants.FormatVersion
            manifest["createdAt"] <- DateTimeOffset.UtcNow.UtcDateTime.ToString("o")
            manifest["damageFile"] <- plan.damage.file
            manifest["damageOffset"] <- plan.damage.byteOffset
            manifest["reason"] <- plan.damage.reason
            let files = JsonArray()
            let plannedActions = JsonArray()
            for f in plan.affectedFiles do
                let name = Path.GetFileName f
                let copyPath = Path.Combine(originalDir, name)
                // 复制原文件（损坏现场保留）
                File.Copy(f, copyPath, false)
                let origSha = sha256OfFile f
                let copySha = sha256OfFile copyPath
                if origSha <> copySha then
                    failwithf "recovery bundle hash mismatch for %s (%s != %s)" f origSha copySha
                let entry = JsonObject()
                entry["path"] <- f
                entry["sha256"] <- origSha
                entry["size"] <- FileInfo(f).Length
                files.Add entry
                let action = JsonObject()
                action["path"] <- f
                if f = plan.damage.file then
                    action["action"] <- "truncate"
                    action["offset"] <- plan.damage.byteOffset
                else
                    action["action"] <- "delete"
                plannedActions.Add action
            manifest["files"] <- files
            manifest["plannedActions"] <- plannedActions
            // 写 diagnosis.json
            let diagnosis = JsonObject()
            let damage = JsonObject()
            damage["file"] <- plan.damage.file
            damage["offset"] <- plan.damage.byteOffset
            damage["reason"] <- plan.damage.reason
            diagnosis["damage"] <- damage
            diagnosis["affectedFiles"] <- files.DeepClone()
            File.WriteAllText(Path.Combine(dir, "diagnosis.json"), diagnosis.ToJsonString(JsonSerializerOptions(JsonSerializerDefaults.General)))
            // 写 manifest.json
            File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest.ToJsonString(JsonSerializerOptions(JsonSerializerDefaults.General)))
            // flush 文件与目录元数据（尽力而为；平台不支持时忽略）
            try
                use mfs = new FileStream(Path.Combine(dir, "manifest.json"), FileMode.Open, FileAccess.ReadWrite)
                mfs.Flush(true)
            with _ -> ()
            Ok dir
        with e ->
            Error(sprintf "recovery bundle creation failed: %s" e.Message)
