﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MimeTypes;
using NovaAPI.Attri;
using NovaAPI.DataTypes;
using NovaAPI.Util;
using static NovaAPI.Util.StorageUtil;

namespace NovaAPI.Controllers
{
    [ApiController]
    public class MediaController : ControllerBase
    {
        readonly NovaChatDatabaseContext Context;
        
        readonly HttpClientHandler handler = new HttpClientHandler()
        {
            SslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            ServerCertificateCustomValidationCallback = ((message, certificate2, arg3, arg4) => true)
        };
        
        public MediaController(NovaChatDatabaseContext context)
        {
            Context = context;
        }

        // User related
        [HttpGet("/User/{user_uuid}/Avatar")]
        public ActionResult GetAvatar(string user_uuid, int size = -1, bool keepAspect = false)
        {
            MediaFile file = RetreiveFile(MediaType.Avatar, user_uuid);
            if (file == null)
                return StatusCode(404, "Unable to find user avatar. Perhaps they only have default one set?");
            MemoryStream ms = new();
            size = size == -1 ? int.MaxValue : size;
            Image img = Image.FromStream(file.File);
            string mimeType = RetreiveMimeType(img);
            if (mimeType != "image/gif")
            {
                ResizeImage(img, size, size, keepAspect).Save(ms, ImageFormat.Png);
                return File(ms.ToArray(), "image/png");
            }
            else
            {
                img.Save(ms, ImageFormat.Gif);
                return File(ms.ToArray(), "image/gif");
            }
        }

        [HttpHead("/User/{user_uuid}/Avatar")]
        public ActionResult HeadAvatar(string user_uuid, int size = -1, bool keepAspect = false)
        {
            MediaFile file = RetreiveFile(MediaType.Avatar, user_uuid);
            MemoryStream ms = new();
            size = size == -1 ? int.MaxValue : size;
            Image img = Image.FromStream(file.File);
            ResizeImage(img, size, size, keepAspect).Save(ms, ImageFormat.Png);
            Response.ContentLength = ms.Length;
            Response.ContentType = file.Meta.MimeType;
            Response.Headers.Add("Access-Control-Allow-Origin", "*");
            return StatusCode(200);
        }

        [HttpPost("/User/{user_uuid}/Avatar")]
        [TokenAuthorization]
        public ActionResult SetAvatar(string user_uuid, IFormFile file)
        {
            //if (!file.ContentType.Contains("image/")) return StatusCode(400);
            DeleteFile(MediaType.Avatar, user_uuid);
            string filename = StoreFile(MediaType.Avatar, file.OpenReadStream(), new AvatarMeta(file.FileName, file.Length, user_uuid));
            return (filename == "")? StatusCode(200) : StatusCode(404);
        }

        // Channel (Group) related 
        [HttpGet("/Channel/{channel_uuid}/Icon")]
        public ActionResult GetChannelAvatar(string channel_uuid, int size = -1, bool keepAspect = false)
        {
            MediaFile file = RetreiveFile(MediaType.ChannelIcon, channel_uuid);
            if (file == null) return StatusCode(404);
            MemoryStream ms = new();
            size = size == -1 ? int.MaxValue : size;
            ResizeImage(Image.FromStream(file.File), size, size, keepAspect).Save(ms, ImageFormat.Png);
            return File(ms.ToArray(), "image/png");
        }

        [HttpHead("/Channel/{channel_uuid}/Icon")]
        public ActionResult HeadChannelAvatar(string channel_uuid, int size = -1, bool keepAspect = false)
        {
            MediaFile file = RetreiveFile(MediaType.ChannelIcon, channel_uuid);
            if (file == null) return StatusCode(404);
            MemoryStream ms = new();
            size = size == -1 ? int.MaxValue : size;
            ResizeImage(Image.FromStream(file.File), size, size, keepAspect).Save(ms, ImageFormat.Png);
            Response.ContentLength = ms.Length;
            Response.ContentType = file.Meta.MimeType;
            Response.Headers.Add("Access-Control-Allow-Origin", "*");
            return StatusCode(200);
        }

        [HttpPost("/Channel/{channel_uuid}/Icon")]
        [TokenAuthorization]
        public ActionResult SetChannelAvatar(string channel_uuid, IFormFile file) {
            DeleteFile(MediaType.ChannelIcon, channel_uuid);
            StoreFile(MediaType.ChannelIcon, file.OpenReadStream(), new IconMeta(file.FileName, file.Length, channel_uuid));
            return StatusCode(200);
        }
        
        [HttpGet("/Channel/{channel_uuid}/{content_id}")]
        public ActionResult GetContent(string channel_uuid, string content_id)
        {
            string path = Path.Combine(ChannelContent, channel_uuid, content_id);
            if (!System.IO.File.Exists(path)) return StatusCode(404);
            MediaFile file = RetreiveFile(MediaType.ChannelContent, content_id, channel_uuid);
            Response.Headers.Add("Access-Control-Allow-Origin", "*");
            return File(file.File, file.Meta.MimeType);
        }

        [HttpGet("/channel/{channel_uuid}/{content_id}/Keys")]
        public ActionResult<string> GetContentKeys(string channel_uuid, string content_id)
        {
            Response.Headers.Add("Access-Control-Allow-Origin", "*");
            return RetreiveChannelMediaKeys(content_id);
        }
   
        [HttpHead("/Channel/{channel_uuid}/{content_id}")]
        public ActionResult HeadContent(string channel_uuid, string content_id)
        {
            string path = Path.Combine(ChannelContent, channel_uuid, content_id);
            if (!System.IO.File.Exists(path)) return StatusCode(404);
            MediaFile file = RetreiveFile(MediaType.ChannelContent, content_id, channel_uuid);
            Response.ContentLength = file.Meta.Filesize;
            Response.ContentType = file.Meta.MimeType;
            Response.Headers.Add("Access-Control-Allow-Origin", "*");
            return StatusCode(200);
        }

        // Content Related
        [HttpPost("/Channel/{channel_uuid}")]
        [TokenAuthorization]
        public ActionResult<string> PostContent(string channel_uuid, IFormFile file, [FromForm] string keys, [FromForm] string iv, string contentToken, string fileType, int width=0, int height=0)
        {
            if (!TokenManager.UseToken(contentToken, channel_uuid))
            {
                TokenManager.InvalidateToken(contentToken);
                return StatusCode(400, "The provided Content Token has expired or is for another channel. Please request a new one.");
            }
            string user_uuid = Context.GetUserUUID(this.GetToken());
            if (!ChannelUtils.CheckUserChannelAccess(user_uuid, channel_uuid)) return StatusCode(403);
            if (file.Length >= 20971520 || file.Length == 0) return StatusCode(413, $"File must be > 0MB and <= 20MB ({file.Length}bytes)");

            string contentID = StoreFile(MediaType.ChannelContent, file.OpenReadStream(), new ChannelContentMeta(width, height, MimeTypeMap.GetMimeType(fileType), file.FileName, channel_uuid, Context.GetUserUUID(this.GetToken()), file.Length, keys, iv));
            if (contentID == "") return StatusCode(500);
            TokenManager.AddID(contentToken, contentID);
            return contentID;
        }

        [HttpGet("/Proxy")]
        public async Task<ActionResult> ProxyUrl(string url)
        {
            HttpClient client = new HttpClient(handler);
            foreach (string key in Request.Headers.Keys)
            {
                if (key != "jwt") continue;
                client.DefaultRequestHeaders.Add(key, (string) Request.Headers[key]);
            }

            HttpResponseMessage resp = await client.GetAsync(url);
            foreach (KeyValuePair<string, IEnumerable<string>> header in resp.Headers)
            {
                if (header.Key != "Content-Type" && header.Key != "Content-Length") continue;
                Response.Headers.Add(header.Key, string.Join(" ", header.Value));
            }
            
            Console.WriteLine(resp.StatusCode);
            return StatusCode((int)resp.StatusCode, (await resp.Content.ReadAsStreamAsync()));
        }
        
        [HttpHead("/Proxy")]
        public async Task<ActionResult> HeadProxyUrl(string url)
        {
            HttpClient client = new HttpClient(handler);
            foreach (string key in Request.Headers.Keys)
            {
                if (key != "jwt") continue;
                client.DefaultRequestHeaders.Add(key, (string) Request.Headers[key]);
            }

            HttpResponseMessage resp = await client.GetAsync(url);
            foreach (KeyValuePair<string, IEnumerable<string>> header in resp.Headers)
            {
                if (header.Key != "Content-Type" && header.Key != "Content-Length") continue;
                Response.Headers.Add(header.Key, string.Join(" ", header.Value));
            }
            
            return StatusCode((int)resp.StatusCode);
        }
        
        [HttpPost("/Proxy")]
        public async Task<ActionResult> PostProxyURL(string url)
        {
            HttpClient client = new HttpClient(handler);
            string ct = "";
            foreach (string key in Request.Headers.Keys)
            {
                if (key == "Content-Type") ct = Request.Headers[key];
                if (key != "jwt") continue;
                // Copy Request Headers
                client.DefaultRequestHeaders.Add(key, (string) Request.Headers[key]);
            }
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(ct));

            MemoryStream ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            ByteArrayContent content = new ByteArrayContent(ms.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue(ct);
            ms.Close();
            await ms.DisposeAsync();
            HttpResponseMessage resp = await client.PostAsync(url, content);
            return StatusCode((int)resp.StatusCode, (await resp.Content.ReadAsStreamAsync()));
        }
        

        [HttpGet("/Channel/{channel_uuid}/RequestContentToken")]
        [TokenAuthorization]
        public ActionResult<string> GenerateToken(string channel_uuid, int uploads)
        {
            if (!ChannelUtils.ChannelExsists(channel_uuid))
                return StatusCode(404, $"Channel with uuid \"{channel_uuid}\" unknown");
            string token = TokenManager.GenerateToken(Context.GetUserUUID(this.GetToken()), uploads, channel_uuid);
            if (token == "") return StatusCode(413, "Maximum of 10 files per message");
            return token;
        }

        [HttpDelete("/Channel/{channel_uuid}/InvalidateContentToken/{token}")]
        [TokenAuthorization]
        public ActionResult InvalidateToken(string channel_uuid, string token)
        {
            TokenManager.InvalidateToken(token);
            return StatusCode(200);
        }
        
        public static string CreateMD5(string input)
        {
            // Use input string to calculate MD5 hash
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.ASCII.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                // Convert the byte array to hexadecimal string
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("X2"));
                }
                return sb.ToString();
            }
        }
        private static Image ResizeImage(Image img, int maxWidth, int maxHeight, bool keepAspect)
        {
            if (img.Height < maxHeight && img.Width < maxWidth) return img;
            using (img)
            {
                Double xRatio = (double)img.Width / maxWidth;
                Double yRatio = (double)img.Height / maxHeight;
                Double ratio = Math.Max(xRatio, yRatio);
                int nnx = (keepAspect)? (int)Math.Floor(img.Width / ratio) : maxWidth;
                int nny = (keepAspect)? (int)Math.Floor(img.Height / ratio): maxHeight;
                Bitmap cpy = new Bitmap(nnx, nny, PixelFormat.Format32bppArgb);
                using (Graphics gr = Graphics.FromImage(cpy))
                {
                    gr.Clear(Color.Transparent);

                    // This is said to give best quality when resizing images
                    gr.InterpolationMode = InterpolationMode.HighQualityBicubic;

                    gr.DrawImage(img,
                        new Rectangle(0, 0, nnx, nny),
                        new Rectangle(0, 0, img.Width, img.Height),
                        GraphicsUnit.Pixel);
                }
                return cpy;
            }

        }

        private string RetreiveMimeType(Image img)
        {
            ImageFormat format = img.RawFormat;
            ImageCodecInfo codec = ImageCodecInfo.GetImageDecoders().First(c => c.FormatID == format.Guid);
            return codec.MimeType;
        }
    }
}
