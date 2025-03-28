﻿using Microsoft.AspNetCore.Mvc;
using MySql.Data.MySqlClient;
using NovaAPI.Models;
using NovaAPI.Util;
using System;
using System.Net;
using System.Net.Mail;

namespace NovaAPI.Controllers
{
    [Route("Auth")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly NovaChatDatabaseContext Context;

        public AuthController(NovaChatDatabaseContext context)
        {
            Context = context;
        }

        [HttpPost("Login")]
        public ActionResult<ReturnLoginUserInfo> AuthUser(LoginUserInfo info)
        {
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();

            using MySqlCommand cmd = new($"SELECT * FROM Users WHERE (Email=@email)", conn);
            cmd.Parameters.AddWithValue("@email", EncryptionUtils.GetHashString(info.Email));
            
            MySqlDataReader reader = cmd.ExecuteReader();
            
            while (reader.Read())
            {
                if (reader["Confirmed"].ToString() == "0") return StatusCode(405);
                string saltedPassword = EncryptionUtils.GetSaltedHashString(info.Password, (byte[])reader["Salt"]);
                if (reader["Password"].ToString() == saltedPassword)
                {
                    return new ReturnLoginUserInfo
                    {
                        UUID = reader["UUID"].ToString(),
                        Token = reader["Token"].ToString(),
                        PublicKey = reader["PubKey"].ToString(),
                        Key = new AESMemoryEncryptData
                        {
                            Content = reader["PrivKey"].ToString(),
                            IV = reader["IV"].ToString()
                        }
                    };
                }
                else
                {
                    reader.Close();
                    return StatusCode(403, $"Unable to authenticate user with email: {info.Email}");
                }
            }
            return StatusCode(404, $"Unable to find user with email {info.Email}");
        }

        [HttpPost("Register")]
        public ActionResult RegisterUser(CreateUserInfo info)
        {
            if (string.IsNullOrEmpty(info.Password) || string.IsNullOrEmpty(info.Email) || string.IsNullOrEmpty(info.Username)) return StatusCode(400, "Password/Email/User cannot be empty");
            if (info.Username.Length > 24) return StatusCode(413, "Username length greater than 24 characters");
            string UUID = Guid.NewGuid().ToString("N");
            string email = EncryptionUtils.GetHashString(info.Email);
            string token = EncryptionUtils.GetSaltedHashString(UUID + info.Email + EncryptionUtils.GetHashString(info.Password) + info.Username + DateTime.Now, EncryptionUtils.GetSalt(8));
            using (MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master))
            {
                conn.Open();
                using MySqlCommand dis = new($"SELECT `GetRandomDiscriminator`(@user) AS `GetRandomDiscriminator`", conn);
                dis.Parameters.AddWithValue("@user", info.Username);
                MySqlDataReader reader = dis.ExecuteReader();
                string disc = null;
                while (reader.Read()) disc = reader["GetRandomDiscriminator"].ToString();
                reader.Close();

                using MySqlCommand checkForEmail = new($"SELECT * From Users WHERE (Email=@email)", conn);
                checkForEmail.Parameters.AddWithValue("@email", email);
                MySqlDataReader read = checkForEmail.ExecuteReader();
                if (read.HasRows) return StatusCode(409);

                conn.Close();
                conn.Open();

                // Get Salt - Sometimes things are better with a bit of salt
                byte[] salt = EncryptionUtils.GetSalt(64);

                using MySqlCommand cmd = new($"INSERT INTO Users (UUID, Username, Discriminator, Password, Salt, Email, Token, Avatar, PubKey, PrivKey, IV, Confirmed) VALUES (@uuid, @user, @disc, @pass, @salt, @email, @tok, @avatar, @pubKey, @privKey, @iv, @confirm)", conn);
                cmd.Parameters.AddWithValue("@uuid", UUID);
                cmd.Parameters.AddWithValue("@user", info.Username);
                cmd.Parameters.AddWithValue("@disc", disc);
                cmd.Parameters.AddWithValue("@pass", EncryptionUtils.GetSaltedHashString(info.Password, salt));
                cmd.Parameters.AddWithValue("@salt", salt);
                cmd.Parameters.AddWithValue("@email", email);
                cmd.Parameters.AddWithValue("@tok", token);
                cmd.Parameters.AddWithValue("@avatar", "");
                cmd.Parameters.AddWithValue("@pubKey", info.Key.Pub);
                cmd.Parameters.AddWithValue("@privKey", info.Key.Priv);
                cmd.Parameters.AddWithValue("@iv", info.Key.PrivIV);
                cmd.Parameters.AddWithValue("@confirm", !EmailConfig.VerifyEmail);
                cmd.ExecuteNonQuery();

                using MySqlConnection users = MySqlServer.CreateSQLConnection(Database.User);
                users.Open();
                
                using MySqlCommand createTable = new($"CREATE TABLE `{UUID}` (Id INT NOT NULL AUTO_INCREMENT, Property CHAR(255) NOT NULL, Value VARCHAR(1000) NOT NULL, PRIMARY KEY(`Id`)) ENGINE = InnoDB;", users);
                createTable.ExecuteNonQuery();

                using MySqlCommand createKeystore = new($"CREATE TABLE `{UUID}_keystore` (UUID CHAR(255) NOT NULL , PubKey VARCHAR(1000) NOT NULL , PRIMARY KEY (`UUID`)) ENGINE = InnoDB;", users);
                createKeystore.ExecuteNonQuery();
                using MySqlCommand addTimestamp = new($"INSERT INTO `{UUID}_keystore` (UUID, Pubkey) VALUES (@field, @value)", users);
                addTimestamp.Parameters.AddWithValue("@field", "Timestamp");
                addTimestamp.Parameters.AddWithValue("@value", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                addTimestamp.ExecuteNonQuery();

                using MySqlCommand createFriends = new($"CREATE TABLE `{UUID}_friends` (Id INT NOT NULL AUTO_INCREMENT, UUID CHAR(255) NOT NULL, State CHAR(255) NOT NULL, PRIMARY KEY (`Id`), UNIQUE (`UUID`)) ENGINE = InnoDB;", users);
                createFriends.ExecuteNonQuery();
                
                users.Close();
            }
            
            // Send confirmation email
            if (EmailConfig.VerifyEmail)
            {
                MailMessage message = new MailMessage();
                SmtpClient smtp = new SmtpClient();
                message.From = new MailAddress(EmailConfig.FromAddress);
                message.To.Add(new MailAddress(email));
                message.Subject = "Email confirmation";
                message.IsBodyHtml = true;
                message.Body =
                    $"Please use the following link to <a href=\"https://{Startup.Interface_Domain}/auth/confirm?token={token}\">confirm your email</a><br>If you didn't create a Orbit account you can disregard this email";
                smtp.Port = EmailConfig.SMTPPort;
                smtp.Host = EmailConfig.SMTPHost;
                smtp.EnableSsl = true;
                smtp.UseDefaultCredentials = false;
                smtp.Credentials = new NetworkCredential(EmailConfig.Username, EmailConfig.Password);
                smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                smtp.Send(message);
            }


            return StatusCode(200, "User created");
        }

        [HttpPost("RequestReset")]
        public ActionResult ResetPassword(string email)
        {
            if (!EmailConfig.PasswordReset) return StatusCode(400, "This server has password resetting disabled.");
            using MySqlConnection conn = MySqlServer.CreateSQLConnection(Database.Master);
            conn.Open();
            using MySqlCommand checkForEmail = new($"SELECT * From Users WHERE (Email=@email)", conn);
            checkForEmail.Parameters.AddWithValue("@email", EncryptionUtils.GetHashString(email));
            MySqlDataReader read = checkForEmail.ExecuteReader();
            if (!read.HasRows) return StatusCode(404);

            read.Read();

            string token = ResetTokenController.GenerateToken((string) read["UUID"]);
            
            MailMessage message = new MailMessage();  
            SmtpClient smtp = new SmtpClient();  
            message.From = new MailAddress(EmailConfig.FromAddress);
            message.To.Add(new MailAddress(email));
            message.Subject = "Requested Password Reset";  
            message.IsBodyHtml = true;
            message.Body = $"Please use the following link to <a href=\"https://{Startup.Interface_Domain}/auth/reset?token={token}\">reset your password</a><br>. This link will expire in 10 minutes. If you didn't request a reset, you can safely disregard this email.";  
            smtp.Port = EmailConfig.SMTPPort;
            smtp.Host = EmailConfig.SMTPHost;
            smtp.EnableSsl = true;  
            smtp.UseDefaultCredentials = false;  
            smtp.Credentials = new NetworkCredential(EmailConfig.Username, EmailConfig.Password);
            smtp.DeliveryMethod = SmtpDeliveryMethod.Network;  
            smtp.Send(message);  
            
            conn.Close();
            return StatusCode(200);
        }
    }
}
