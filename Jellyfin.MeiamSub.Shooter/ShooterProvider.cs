﻿using Jellyfin.MeiamSub.Shooter.Model;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Jellyfin.MeiamSub.Shooter
{
    /// <summary>
    /// 迅雷字幕组件
    /// </summary>
    public class ShooterProvider : ISubtitleProvider, IHasOrder
    {
        #region 变量声明
        public const string ASS = "ass";
        public const string SSA = "ssa";
        public const string SRT = "srt";

        private readonly ILogger<ShooterProvider> _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private static readonly HttpClient Client = new HttpClient();

        public int Order => 0;
        public string Name => "MeiamSub.Shooter";

        /// <summary>
        /// 支持电影、剧集
        /// </summary>
        public IEnumerable<VideoContentType> SupportedMediaTypes => new List<VideoContentType>() { VideoContentType.Movie, VideoContentType.Episode };
        #endregion

        #region 构造函数
        public ShooterProvider(ILogger<ShooterProvider> logger, IJsonSerializer jsonSerializer)
        {
            _logger = logger;
            _jsonSerializer = jsonSerializer;
        }
        #endregion

        #region 查询字幕

        /// <summary>
        /// 查询请求
        /// </summary>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"MeiamSub.Shooter Search | Request -> { _jsonSerializer.SerializeToString(request) }");

            var subtitles = await SearchSubtitlesAsync(request);

            return subtitles;
        }

        /// <summary>
        /// 查询字幕
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        private async Task<IEnumerable<RemoteSubtitleInfo>> SearchSubtitlesAsync(SubtitleSearchRequest request)
        {
            if (request.Language != "chi" && request.Language != "eng")
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }

            FileInfo fileInfo = new FileInfo(request.MediaPath);

            var hash = ComputeFileHash(fileInfo);

            using (var _httpClient = new HttpClient())
            {
                var options = new {

                    filehash = HttpUtility.UrlEncode(hash),
                    pathinfo = HttpUtility.UrlEncode(request.MediaPath),
                    format = "json",
                    lang = request.Language == "chi" ? "chn" : "eng"
                };

                _logger.LogInformation($"MeiamSub.Shooter Search | Request -> { _jsonSerializer.SerializeToString(options) }");

                var response = await PostAsync($"http://www.shooter.cn/api/subapi.php", options);

                _logger.LogInformation($"MeiamSub.Shooter Search | Response -> { _jsonSerializer.SerializeToString(response) }");

                if (response.StatusCode == HttpStatusCode.OK && response.Headers.Any(m => m.Value.Contains("application/json")))
                {
                    var subtitleResponse = _jsonSerializer.DeserializeFromStream<List<SubtitleResponseRoot>>(await response.Content.ReadAsStreamAsync());

                    if (subtitleResponse != null)
                    {
                        _logger.LogInformation($"MeiamSub.Shooter Search | Response -> { _jsonSerializer.SerializeToString(subtitleResponse) }");

                        var remoteSubtitleInfos = new List<RemoteSubtitleInfo>();

                        foreach (var subFileInfo in subtitleResponse)
                        {
                            foreach (var subFile in subFileInfo.Files)
                            {
                                remoteSubtitleInfos.Add(new RemoteSubtitleInfo()
                                {
                                    Id = Base64Encode(_jsonSerializer.SerializeToString(new DownloadSubInfo
                                    {
                                        Url = subFile.Link,
                                        Format = subFile.Ext,
                                        Language = request.Language,
                                        TwoLetterISOLanguageName = request.TwoLetterISOLanguageName,
                                    })),
                                    Name = $"[MEIAMSUB] { Path.GetFileName(request.MediaPath) } | {request.TwoLetterISOLanguageName} | 射手",
                                    Author = "Meiam ",
                                    ProviderName = "MeiamSub.Shooter",
                                    Format = subFile.Ext,
                                    Comment = $"Format : { ExtractFormat(subFile.Ext)}"
                                });
                            }
                        }

                        _logger.LogInformation($"MeiamSub.Shooter Search | Summary -> Get  { remoteSubtitleInfos.Count }  Subtitles");

                        return remoteSubtitleInfos;
                    }
                }

                _logger.LogInformation($"MeiamSub.Shooter Search | Summary -> Get  0  Subtitles");

            }
            return Array.Empty<RemoteSubtitleInfo>();
        }
        #endregion

        #region 下载字幕
        /// <summary>
        /// 下载请求
        /// </summary>
        /// <param name="id"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                _logger.LogInformation($"MeiamSub.Shooter DownloadSub | Request -> {id}");
            });

            return await DownloadSubAsync(id);
        }

        /// <summary>
        /// 下载字幕
        /// </summary>
        /// <param name="info"></param>
        /// <returns></returns>
        private async Task<SubtitleResponse> DownloadSubAsync(string info)
        {
            var downloadSub = _jsonSerializer.DeserializeFromString<DownloadSubInfo>(Base64Decode(info));

            downloadSub.Url = downloadSub.Url.Replace("https://www.shooter.cn", "http://www.shooter.cn");

            _logger.LogInformation($"MeiamSub.Shooter DownloadSub | Url -> { downloadSub.Url }  |  Format -> { downloadSub.Format } |  Language -> { downloadSub.Language } ");

            using (var _httpClient = new HttpClient())
            {

                var response = await _httpClient.GetAsync(downloadSub.Url);

                _logger.LogInformation($"MeiamSub.Shooter DownloadSub | Response -> { response.StatusCode }");

                if (response.StatusCode == HttpStatusCode.OK)
                {

                    return new SubtitleResponse()
                    {
                        Language = downloadSub.Language,
                        IsForced = false,
                        Format = downloadSub.Format,
                        Stream = await response.Content.ReadAsStreamAsync(),
                    };
                }
            }
            return new SubtitleResponse();

        }
        #endregion

        #region 内部方法

        /// <summary>
        /// Base64 加密
        /// </summary>
        /// <param name="plainText">明文</param>
        /// <returns></returns>
        public static string Base64Encode(string plainText)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }
        /// <summary>
        /// Base64 解密
        /// </summary>
        /// <param name="base64EncodedData"></param>
        /// <returns></returns>
        public static string Base64Decode(string base64EncodedData)
        {
            var base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
            return Encoding.UTF8.GetString(base64EncodedBytes);
        }

        /// <summary>
        /// 提取格式化字幕类型
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        protected string ExtractFormat(string text)
        {

            string result = null;

            if (text != null)
            {
                text = text.ToLower();
                if (text.Contains(ASS)) result = ASS;
                else if (text.Contains(SSA)) result = SSA;
                else if (text.Contains(SRT)) result = SRT;
                else result = null;
            }
            return result;
        }

        /// <summary>
        /// 获取文件 Hash (射手)
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public static string ComputeFileHash(FileInfo fileInfo)
        {
            string ret = "";

            if (!fileInfo.Exists || fileInfo.Length < 8 * 1024)
            {
                return ret;
            }

            FileStream fs = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read);

            long[] offset = new long[4];
            offset[3] = fileInfo.Length - 8 * 1024;
            offset[2] = fileInfo.Length / 3;
            offset[1] = fileInfo.Length / 3 * 2;
            offset[0] = 4 * 1024;

            byte[] bBuf = new byte[1024 * 4];

            for (int i = 0; i < 4; ++i)
            {
                fs.Seek(offset[i], 0);
                fs.Read(bBuf, 0, 4 * 1024);

                MD5 md5Hash = MD5.Create();
                byte[] data = md5Hash.ComputeHash(bBuf);
                StringBuilder sBuilder = new StringBuilder();

                for (int j = 0; j < data.Length; j++)
                {
                    sBuilder.Append(data[j].ToString("x2"));
                }

                if (!string.IsNullOrEmpty(ret))
                {
                    ret += ";";
                }

                ret += sBuilder.ToString();
            }

            fs.Close();

            return ret;
        }
        #endregion

        #region HTTP
        public async Task<HttpResponseMessage> PostAsync(string url, object data)
        {
            string content = _jsonSerializer.SerializeToString(data);
            var buffer = Encoding.UTF8.GetBytes(content);
            var byteContent = new ByteArrayContent(buffer);
            byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return await Client.PostAsync(url, byteContent);
        }
        #endregion
    }
}