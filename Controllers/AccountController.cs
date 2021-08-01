using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TodoApp.Configurations;
using TodoApp.Data;
using TodoApp.Models;
using TodoApp.Models.DTOs.Requests;
using TodoApp.Models.DTOs.Responses;

namespace TodoApp.Controllers
{
    [Route("api/{controller}")]
    [ApiController]
    public class AccountController : ControllerBase
    {
        private readonly UserManager<IdentityUser> _userManager;
        private readonly JWTConfig _jwtConfig;
        private readonly TokenValidationParameters _TokenValidationParams;
        private readonly APIDBContext _apiDbContext;
        
        public AccountController(
            UserManager<IdentityUser> userManager, 
            IOptionsMonitor<JWTConfig> optionsMonitor,
            TokenValidationParameters tokenValidationParams,
            APIDBContext apiDbContext
            ) 
        {
            _userManager = userManager;
            _jwtConfig = optionsMonitor.CurrentValue;
            _TokenValidationParams = tokenValidationParams;
            _apiDbContext = apiDbContext;
        }

        [HttpPost]
        [Route("register")]
        public async Task<IActionResult> Register([FromBody]UserRegistrationModel data) 
        {
            if (ModelState.IsValid) 
            {
                var user = new IdentityUser() {
                    Email = data.Email,
                    UserName = data.Username
                };
                
                var isCreated = await _userManager.CreateAsync(user, data.Password);
                if (isCreated.Succeeded) 
                {
                    var jwtToken = await GenerateJWTToken(user);
                    return Ok(jwtToken);
                };

                return BadRequest(new UserRegistrationRensponseModel() {
                    Errors = isCreated.Errors.Select(x => x.Description).ToList(),
                    Success = false
                });
            }
            return BadRequest(new UserRegistrationRensponseModel() {
                Errors = new List<string>() {
                    "Invalid payload"
                },
                Success = false
            });
        }

        [HttpPost]
        [Route("login")]
        public async Task<IActionResult> Login([FromBody]UserLoginModel data) 
        {
            if (ModelState.IsValid) 
            {
                var userExist = await _userManager.FindByNameAsync(data.Username);
                if (userExist == null) 
                {
                    return BadRequest(new UserLoginRensponseModel() {
                        Errors = new List<string>() {
                            "Invalid Username or Password"
                        },
                        Success = false
                    });
                };

                var validateLoginInfo = await _userManager.CheckPasswordAsync(userExist, data.Password);
                if (validateLoginInfo) 
                {
                    var jwtToken = await GenerateJWTToken(userExist);
                    return Ok(jwtToken);
                };

                return BadRequest(new UserLoginRensponseModel() {
                    Errors = new List<string>() {
                        "Invalid Username or Password"
                    },
                    Success = false
                });
            }
            return BadRequest(new UserLoginRensponseModel() {
                Errors = new List<string>() {
                    "Invalid payload"
                },
                Success = false
            });
        }

        [HttpPost]
        [Route("RefreshToken")]
        public async Task<IActionResult> RefreshToken([FromBody] TokenRequestModel tokenRequest)
        {
            if (ModelState.IsValid) 
            {
                var result = await VerifyToken(tokenRequest);
                if (result == null) {
                    return StatusCode(400, "Invalid Token");
                };
                return Ok(result);
            }
            return StatusCode(400, "Invalid Payload");
        }

        private async Task<AuthConfig> VerifyToken(TokenRequestModel tokenRequest) 
        {
            var jwtTokenHandler = new JwtSecurityTokenHandler();
            try
            {   
                // Validation 1 - Validate JWT token format
                var tokenInVerification = jwtTokenHandler.ValidateToken(tokenRequest.Token, _TokenValidationParams, out var validatedToken);
                
                // Validation 2 - Validate encryption algorithm
                if (validatedToken is JwtSecurityToken jwtSecurityToken) 
                {
                    var result = jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase);
                    if (!result) return null;
                };

                // Validation 3 - Validate expiry date
                var utcExpiryDate = long.Parse(tokenInVerification.Claims.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Exp).Value);
                var expiryDate = UnixTimeStampDateTime(utcExpiryDate);
                if (expiryDate > DateTime.UtcNow)
                {
                    return new AuthConfig() {
                        Success = false,
                        Errors = new List<string>() {
                            "Token masih aktif."
                        }
                    };
                };
                
                // Validation 4 - Validate existing of the token
                var storedToken = await _apiDbContext.RefreshTokens.FirstOrDefaultAsync(x => x.Token == tokenRequest.RefreshToken);
                if (storedToken == null) 
                {
                    return new AuthConfig() {
                        Success = false,
                        Errors = new List<string>() {
                            "Token tidak ditemukan."
                        }
                    };
                };

                // Validation 5 - Validate if token is not yet used
                if (storedToken.IsUsed)
                {
                    return new AuthConfig() {
                        Success = false,
                        Errors = new List<string>() {
                            "Token telah digunakan."
                        }
                    };
                };

                // Validate 6 - Validate if not revoked
                if (storedToken.IsRevoked) {
                    return new AuthConfig() {
                        Success = false,
                        Errors = new List<string>() {
                            "Token dicabut."
                        }
                    };
                };

                // Validate 7 - Validate the id
                var jti = tokenInVerification.Claims.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti).Value;
                if (storedToken.JWTId != jti) 
                {
                    return new AuthConfig() {
                        Success = false,
                        Errors = new List<string>() {
                            "Token salah."
                        }
                    };
                }

                // Update token
                storedToken.IsUsed = true;
                _apiDbContext.RefreshTokens.Update(storedToken);
                await _apiDbContext.SaveChangesAsync();

                // Generate a new token
                var dbUser = await _userManager.FindByIdAsync(storedToken.UserId);
                return await GenerateJWTToken(dbUser);
            }
            catch (Exception)
            {
               return null;
            }
        }

        private DateTime UnixTimeStampDateTime(long unixTimeStamp) 
        {
            var dateTimeVal = new DateTime(1970,1,1,0,0,0,0, DateTimeKind.Utc);
            dateTimeVal = dateTimeVal.AddSeconds(unixTimeStamp).ToUniversalTime();
            return dateTimeVal;
        }

        private async Task<AuthConfig> GenerateJWTToken(IdentityUser user)
        {
            var jwtTokenHandler = new JwtSecurityTokenHandler();
            var secret = Encoding.ASCII.GetBytes(_jwtConfig.SignInSecret);

            var tokenDescriptor = new SecurityTokenDescriptor 
            {
                Subject = new ClaimsIdentity(new [] {
                    new Claim("Id", user.Id),
                    new Claim(JwtRegisteredClaimNames.Email, user.Email),
                    new Claim(JwtRegisteredClaimNames.Sub, user.Email),
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
                }),
                Expires = DateTime.UtcNow.AddDays(7),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(secret), SecurityAlgorithms.HmacSha256Signature)
            };

            var token = jwtTokenHandler.CreateToken(tokenDescriptor);
            var jwtToken = jwtTokenHandler.WriteToken(token);

            var refreshToken = new RefreshTokenModel() {
              JWTId = token.Id,
              IsUsed = false,
              IsRevoked = false,
              UserId = user.Id,
              CreatedDate = DateTime.UtcNow,
              Exp = DateTime.UtcNow.AddMonths(6),
              Token = RandomString(35) + Guid.NewGuid()
            };

            await _apiDbContext.RefreshTokens.AddAsync(refreshToken);
            await _apiDbContext.SaveChangesAsync();

            return new AuthConfig() {
                Token = jwtToken,
                Success = true,
                RefreshToken = refreshToken.Token
            };
        }

        private string RandomString(int length) {
            var random = new Random();
            var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
            return new string(Enumerable.Repeat(chars, length)
                .Select(x => x[random.Next(x.Length)]).ToArray());
        }
    }
}