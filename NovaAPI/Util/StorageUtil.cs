﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using MimeTypes;
using MySql.Data.MySqlClient;
using NovaAPI.Controllers;
using NovaAPI.DataTypes;
using NovaAPI.Interfaces;

namespace NovaAPI.Util
{
    public static class StorageUtil
    {
        public static string NC3Storage = "";
        public static string UserData = "";
        public static string ChannelData = "";
        public static string ChannelContent = "";
        public static string ChannelIcon = "";

        private static NovaChatDatabaseContext Context;

        public enum MediaType { Avatar, ChannelIcon, ChannelContent }
        public static void InitStorage(string directory, IConfigurationRoot config)
        {
            Context = new NovaChatDatabaseContext(config);
            if (directory == "") directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (directory == null) throw new ArgumentException("directory is null");

            NC3Storage = Path.Combine(directory, "NC3Storage");
            Console.ForegroundColor = ConsoleColor.Green;
            if (!Directory.Exists(NC3Storage))
            {
                Directory.CreateDirectory(NC3Storage);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"Storage ({NC3Storage}) Directory Created");
            }
            else Console.WriteLine($"Found Storage ({NC3Storage}) Directory. Continuing...");

            Console.ForegroundColor = ConsoleColor.Green;

            UserData = Path.Combine(NC3Storage, "UserData");
            if (!Directory.Exists(UserData))
            {
                Directory.CreateDirectory(UserData);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"UserData ({UserData}) Directory Created");
            }
            else Console.WriteLine($"Found UserData ({UserData}) Directory. Continuing...");

            Console.ForegroundColor = ConsoleColor.Green;

            ChannelData = Path.Combine(NC3Storage, "ChannelData");
            if (!Directory.Exists(ChannelData))
            {
                Directory.CreateDirectory(ChannelData);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"ChannelData ({ChannelData}) Directory Created");
            }
            else Console.WriteLine($"Found ChannelData ({ChannelData}) Directory. Continuing...");

            Console.ForegroundColor = ConsoleColor.Green;

            ChannelContent = Path.Combine(ChannelData, "ChannelContent");
            if (!Directory.Exists(ChannelContent))
            {
                Directory.CreateDirectory(ChannelContent);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"ChannelContent ({ChannelContent}) Directory Created");
            }
            else Console.WriteLine($"Found ChannelContent ({ChannelContent}) Directory. Continuing...");

            Console.ForegroundColor = ConsoleColor.Green;

            ChannelIcon = Path.Combine(ChannelData, "ChannelIcon");
            if (!Directory.Exists(ChannelIcon))
            {
                Directory.CreateDirectory(ChannelIcon);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"ChannelIcon ({ChannelIcon}) Directory Created");
            }
            else Console.WriteLine($"Found ChannelIcon ({ChannelIcon}) Directory. Continuing...");

            Console.WriteLine("Data Directory Setup Complete");
            Console.ForegroundColor = ConsoleColor.Gray;
        }
        
        public static string StoreFile(MediaType mediaType, Stream file, IMeta meta)
        {
            if (mediaType == MediaType.ChannelContent)
            {
                ChannelContentMeta filemeta = (ChannelContentMeta) meta;
                string path = Path.Combine(ChannelContent, filemeta.Channel_UUID);
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    Console.WriteLine($"Created Directory ({path}) For Channnel {filemeta.Channel_UUID}");
                }
                
                // Save file to disk
                string filename = GlobalUtils.CreateMD5(filemeta.Filename + DateTime.Now);
                FileStream fs = File.Create(Path.Combine(path, filename));
                file.CopyTo(fs);
                fs.Close();
                
                // Store file meta data
                using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
                conn.Open();
                using MySqlCommand cmd = new($"INSERT INTO ChannelMedia (File_UUID, Channel_UUID, User_UUID, User_Keys, IV, Filename, MimeType, Size, ContentWidth, ContentHeight) VALUES (@uuid, @channel, @user_uuid, @keys, @iv, @filename, @mime, @size, @width, @height)", conn);
                cmd.Parameters.AddWithValue("@uuid", filename);
                cmd.Parameters.AddWithValue("@channel", filemeta.Channel_UUID);
                cmd.Parameters.AddWithValue("@user_uuid", filemeta.User_UUID);
                cmd.Parameters.AddWithValue("@keys", filemeta.Keys);
                cmd.Parameters.AddWithValue("@iv", filemeta.IV);
                cmd.Parameters.AddWithValue("@filename", filemeta.Filename);
                cmd.Parameters.AddWithValue("@mime", filemeta.MimeType);
                cmd.Parameters.AddWithValue("@size", filemeta.Filesize);
                cmd.Parameters.AddWithValue("@width", filemeta.Width);
                cmd.Parameters.AddWithValue("@height", filemeta.Height);
                if (cmd.ExecuteNonQuery() == 0) return "E";
                return filename;
            }
            else if (mediaType == MediaType.Avatar)
            {
                AvatarMeta filemeta = (AvatarMeta) meta;
                string filename = GlobalUtils.CreateMD5(filemeta.Filename + DateTime.Now);
                FileStream fs = File.Create(Path.Combine(UserData, filename));
                file.CopyTo(fs);
                fs.Close();
                
                using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
                conn.Open();
                using MySqlCommand setAvatar = new($"UPDATE Users SET Avatar=@avatar WHERE (UUID=@uuid)", conn);
                setAvatar.Parameters.AddWithValue("@uuid", filemeta.User_UUID);
                setAvatar.Parameters.AddWithValue("@avatar", filename);
                if (setAvatar.ExecuteNonQuery() == 0) return "E";
                conn.Close();
                return "";
            }
            else
            {
                IconMeta filemeta = (IconMeta) meta;
                string filename = GlobalUtils.CreateMD5(filemeta.Filename + DateTime.Now);
                FileStream fs = File.Create(Path.Combine(ChannelIcon, filename));
                file.CopyTo(fs);
                fs.Close();

                using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
                conn.Open();
                using MySqlCommand setAvatar = new($"UPDATE Channels SET ChannelIcon=@avatar WHERE (Table_ID=@channel_uuid)",
                    conn);
                setAvatar.Parameters.AddWithValue("@channel_uuid", filemeta.Channel_UUID);
                setAvatar.Parameters.AddWithValue("@avatar", filename);
                if (setAvatar.ExecuteNonQuery() == 0) return "E";
                conn.Close();
                return "";
            }
        }

        public static MediaFile RetreiveFile(MediaType mediaType, string resource_id, string location_id = "")
        {
            if (mediaType == MediaType.ChannelContent)
            {
                string path = Path.Combine(ChannelContent, location_id, resource_id);
                if (!File.Exists(path)) return null;
                FileStream fs = File.OpenRead(path);
                Diamension dim = RetreiveDiamension(resource_id);
                return new MediaFile(fs,
                    new ChannelContentMeta(dim.Width, dim.Height, RetreiveMimeType(resource_id), RetreiveFilename(resource_id), location_id,
                        RetreiveContentAuthor(resource_id), fs.Length, RetreiveChannelMediaKeys(resource_id), RetreiveChannelMediaIV(resource_id)));
            }
            else if (mediaType == MediaType.ChannelIcon)
            {
                string path = Path.Combine(ChannelIcon, RetreiveChannelIcon(resource_id));
                if (!File.Exists(path)) return null;
                FileStream fs = File.OpenRead(path);
                return new MediaFile(fs, new IconMeta(resource_id, fs.Length, location_id));
            }
            else
            {
                string name = RetreiveUserAvatar(resource_id);
                string path = Path.Combine(UserData, name);
                if (!File.Exists(path)) return null;
                FileStream fs = File.OpenRead(path);
                return new MediaFile(fs, new AvatarMeta(name, fs.Length, resource_id));
            }
        }

        public static void DeleteFile(MediaType mediaType, string resource_id, string location_id = "")
        {
            if (mediaType == MediaType.ChannelContent)
            {
                using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
                conn.Open();
                using MySqlCommand cmd = new("DELETE FROM `ChannelMedia` WHERE (File_UUID=@file)", conn);
                cmd.Parameters.AddWithValue("@file", resource_id);
                cmd.ExecuteNonQuery();
                conn.Close();
                string path = Path.Combine(ChannelContent, location_id, resource_id);
                if (!File.Exists(path)) return;
                File.Delete(path);
            }
            else if (mediaType == MediaType.ChannelIcon)
            {
                string path = Path.Combine(ChannelIcon, RetreiveChannelIcon(resource_id));
                if (!File.Exists(path)) return;
                File.Delete(path);
            }
            else
            {
                string name = RetreiveUserAvatar(resource_id);
                string path = Path.Combine(UserData, name);
                if (!File.Exists(path)) return;
                File.Delete(path);
            }
        }

        public static void RemoveChannelContent(string channel_uuid)
        {
            if (!Directory.Exists(Path.Combine(ChannelContent, channel_uuid))) return;
            string[] files = Directory.GetFiles(Path.Combine(ChannelContent, channel_uuid));
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            foreach (string file in files)
            {
                using MySqlCommand cmd = new("DELETE FROM `ChannelMedia` WHERE (File_UUID=@file)", conn);
                cmd.Parameters.AddWithValue("@file", new FileInfo(file).Name);
                cmd.ExecuteNonQuery();
                File.Delete(Path.Combine(ChannelContent, channel_uuid, file));
            }
            conn.Close();
            Directory.Delete(Path.Combine(ChannelContent, channel_uuid));
        }

        public static void RemoveUserContent(string user_uuid)
        {
            using MySqlConnection dataRead = MySqlServer.CreateSQLConnection(Database.Master);
            dataRead.Open();
            
            using MySqlConnection dataWrite = MySqlServer.CreateSQLConnection(Database.Master);
            dataWrite.Open();

            using MySqlCommand selectUserFiles = new("SELECT * FROM `ChannelMedia` WHERE (User_UUID=@user)", dataRead);
            selectUserFiles.Parameters.AddWithValue("@user", user_uuid);
            MySqlDataReader data = selectUserFiles.ExecuteReader();

            while (data.Read())
            {
                string file_uuid = data["File_UUID"].ToString();
                string channel_uuid = data["Channel_UUID"].ToString();

                if (file_uuid == null || channel_uuid == null) return;

                using MySqlCommand cmd = new("DELETE FROM `ChannelMedia` WHERE (File_UUID=@file)", dataWrite);
                cmd.Parameters.AddWithValue("@file", file_uuid);
                cmd.ExecuteNonQuery();
                if (File.Exists(Path.Combine(ChannelContent, channel_uuid, file_uuid)))
                    File.Delete(Path.Combine(ChannelContent, channel_uuid, file_uuid));
            }
        }

        public static void RemoveSelectChannelContent(string channel_uuid, List<string> contentIds)
        {
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            foreach (string file in contentIds)
            {
                using MySqlCommand cmd = new("DELETE FROM `ChannelMedia` WHERE (File_UUID=@file)", conn);
                cmd.Parameters.AddWithValue("@file", new FileInfo(file).Name);
                cmd.ExecuteNonQuery();
                File.Delete(Path.Combine(ChannelContent, channel_uuid, file));
            }
            conn.Close();
        }
        
        public static string RetreiveMimeType(string content_id)
        {
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            using MySqlCommand cmd = new("SELECT MimeType FROM ChannelMedia WHERE (File_UUID=@uuid)", conn);
            cmd.Parameters.AddWithValue("@uuid", content_id);
            MySqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                return reader["MimeType"].ToString();
            }
            return "";
        }
        
        public static string RetreiveContentAuthor(string content_id)
        {
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            using MySqlCommand cmd = new("SELECT User_UUID FROM ChannelMedia WHERE (File_UUID=@uuid)", conn);
            cmd.Parameters.AddWithValue("@uuid", content_id);
            MySqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                return reader["User_UUID"].ToString();
            }
            return "";
        }

        public static Diamension RetreiveDiamension(string content_id)
        {
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            using MySqlCommand cmd = new("SELECT ContentWidth,ContentHeight FROM ChannelMedia WHERE (File_UUID=@uuid)", conn);
            cmd.Parameters.AddWithValue("@uuid", content_id);
            MySqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                return new Diamension(int.Parse(reader["ContentWidth"].ToString()), int.Parse(reader["ContentHeight"].ToString()));
            }
            return new Diamension(0, 0);
        }

        public static string RetreiveFilename(string content_id)
        {
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            using MySqlCommand cmd = new("SELECT Filename FROM ChannelMedia WHERE (File_UUID=@uuid)", conn);
            cmd.Parameters.AddWithValue("@uuid", content_id);
            MySqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                return reader["Filename"].ToString();
            }
            return "";
        }

        public static string RetreiveUserAvatar(string user_uuid)
        {
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            using MySqlCommand cmd = new($"SELECT Avatar FROM Users WHERE (UUID=@uuid)", conn);
            cmd.Parameters.AddWithValue("@uuid", user_uuid);
            using MySqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                return reader["Avatar"].ToString();
            }

            return "";
        }

        public static string RetreiveChannelIcon(string channel_uuid)
        {
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            using MySqlCommand cmd = new("SELECT ChannelIcon FROM Channels WHERE (Table_ID=@uuid)", conn);
            cmd.Parameters.AddWithValue("@uuid", channel_uuid);
            MySqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                return reader["ChannelIcon"].ToString();
            }
            return "";
        }

        public static string RetreiveChannelMediaKeys(string content_id)
        {
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            using MySqlCommand cmd = new("SELECT User_Keys FROM ChannelMedia WHERE (File_UUID=@uuid)", conn);
            cmd.Parameters.AddWithValue("@uuid", content_id);
            MySqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                return reader["User_Keys"].ToString();
            }
            return "";
        }

        public static string RetreiveChannelMediaIV(string content_id)
        {
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            using MySqlCommand cmd = new("SELECT IV FROM ChannelMedia WHERE (File_UUID=@uuid)", conn);
            cmd.Parameters.AddWithValue("@uuid", content_id);
            MySqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                return reader["IV"].ToString();
            }
            return "";
        }
    }
}
