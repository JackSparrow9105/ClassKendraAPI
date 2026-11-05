using System.Security.Claims;
using System.Text;
using ClassKendraAPI.Data.Entities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ClassKendraAPI.Features.Auth;

public class TokenService(IConfiguration config)
{
    public string Create(User user)
    {
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("phone", user.Phone)
        };
        

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = config["Jwt:Issuer"],
            Audience = config["Jwt:Audience"],
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddDays(config.GetValue("Jwt:TokenDays", 30)),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });
    }
}