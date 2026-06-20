using System.Text.Json.Serialization;

// ReSharper disable PropertyCanBeMadeInitOnly.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable CollectionNeverUpdated.Global

namespace KuaKeDriveWebDav.Quark;

/// <summary>
/// 夸克网盘统一响应基础结构
/// </summary>
public class QuarkResp
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 夸克网盘文件/目录节点
/// </summary>
public class QuarkFile
{
    [JsonPropertyName("fid")]
    public string Fid { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>
    /// true 表示文件，false 表示目录
    /// </summary>
    [JsonPropertyName("file")]
    public bool IsFile { get; set; }

    /// <summary>创建时间（毫秒时间戳）</summary>
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    /// <summary>更新时间（毫秒时间戳）</summary>
    [JsonPropertyName("updated_at")]
    public long UpdatedAt { get; set; }

    public bool IsDirectory => !IsFile;
}

/// <summary>
/// /file/sort 列目录响应
/// </summary>
public class QuarkSortResp : QuarkResp
{
    [JsonPropertyName("data")]
    public QuarkSortData? Data { get; set; }

    [JsonPropertyName("metadata")]
    public QuarkSortMetadata? Metadata { get; set; }
}

public class QuarkSortData
{
    [JsonPropertyName("list")]
    public List<QuarkFile> List { get; set; } = [];
}

public class QuarkSortMetadata
{
    [JsonPropertyName("_total")]
    public int Total { get; set; }
}

/// <summary>
/// /file/download 下载链接响应
/// </summary>
public class QuarkDownloadResp : QuarkResp
{
    [JsonPropertyName("data")]
    public List<QuarkDownloadItem>? Data { get; set; }
}

public class QuarkDownloadItem
{
    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
}
