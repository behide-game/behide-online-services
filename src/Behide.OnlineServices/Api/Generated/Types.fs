namespace rec Behide.OnlineServices.Api.Types

type TokenPairDTO =
    { accessToken: string
      refreshToken: string
      refreshTokenExpiration: System.DateTimeOffset }
    ///Creates an instance of TokenPairDTO with all optional fields initialized to None. The required fields are parameters of this function
    static member Create (accessToken: string, refreshToken: string, refreshTokenExpiration: System.DateTimeOffset): TokenPairDTO =
        { accessToken = accessToken
          refreshToken = refreshToken
          refreshTokenExpiration = refreshTokenExpiration }

[<RequireQualifiedAccess>]
type PostAuthRefreshToken =
    ///OK
    | OK of payload: TokenPairDTO
    ///Bad request
    | BadRequest
    ///Unauthorized
    | Unauthorized
    ///Internal server error
    | InternalServerError
