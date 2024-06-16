module Behide.OnlineServices.Repository.Database

open Behide.OnlineServices
open Behide.OnlineServices.Common

open MongoDB.Bson
open MongoDB.Driver

open System.Threading.Tasks
open FsToolkit.ErrorHandling

let private connectionString = Config.Database.connectionString
let private mongo = connectionString |> MongoClient

let private databaseName = "Behide"
let private database = mongo.GetDatabase databaseName

module Users =
    let private collectionName = "users"
    let collection = database.GetCollection<User> collectionName

    let insert = collection.InsertOneAsync

    // let findByUserId (userId: UserId) : Task<User option> =
    //     let filter = {| ``_id`` = userId |}

    //     filter.ToBsonDocument()
    //     |> BsonDocumentFilterDefinition
    //     |> collection.FindAsync
    //     |> Task.bind (fun users -> users.FirstOrDefaultAsync())
    //     |> Task.map Option.ofNull

    // let findAllByUserEmail (email: Email) : Task<User list> =
    //     let filter = {| ``AuthConnections.Email`` = email |}

    //     filter.ToBsonDocument()
    //     |> BsonDocumentFilterDefinition
    //     |> collection.FindAsync
    //     |> Task.bind (fun users -> users.ToListAsync())
    //     |> Task.map Seq.toList

    let findByUserNameIdentifier (nameIdentifier: string) : Task<User option> =
        let filter = {| ``AuthConnection.Fields.0`` = nameIdentifier |}

        filter.ToBsonDocument()
        |> BsonDocumentFilterDefinition
        |> collection.FindAsync
        |> Task.bind (fun users -> users.FirstOrDefaultAsync())
        |> Task.map Option.ofNull

    // let findAllByUserNameIdentifier (nameIdentifier: string) : Task<User list> =
    //     let filter = {| ``AuthConnections.NameIdentifier`` = nameIdentifier |}

    //     filter.ToBsonDocument()
    //     |> BsonDocumentFilterDefinition
    //     |> collection.FindAsync
    //     |> Task.bind (fun users -> users.ToListAsync())
    //     |> Task.map Seq.toList

    // let updateTokenHashes userId (accessTokenHash: string) (refreshTokenHash: string) =
    //     let filter = {| ``_id.UserId`` = userId |> UserId.raw |}
    //     let update = {| ``$set`` = {|
    //         AccessTokenHash = accessTokenHash
    //         RefreshTokenHash = refreshTokenHash
    //     |} |}

    //     collection.UpdateOneAsync(
    //         filter.ToBsonDocument(),
    //         update.ToBsonDocument()
    //     )
    //     |> TaskResult.simpleCatch (fun exn -> sprintf "Repository error, failed to update user tokens: %s" (exn.ToString()))
    //     |> TaskResult.map ignore

    // let getAuthConnections userId =
    //     let aggregation =[|
    //         {| ``$match`` = {| ``_id.UserId`` = userId |> UserId.raw |} |}.ToBsonDocument()
    //         {| ``$project`` =
    //             {| ``_id`` = 0
    //                AuthConnections = 1 |} |}.ToBsonDocument()
    //     |]

    //     aggregation
    //     |> BsonDocumentStagePipelineDefinition
    //     |> collection.AggregateAsync<{| AuthConnections: AuthConnection array |}>
    //     |> TaskResult.simpleCatch (fun exn -> sprintf "Repository error, failed to retrieve auth connections: %s" (exn.ToString()))
    //     |> Task.bind (fun res -> task {
    //         match res with
    //         | Ok x ->
    //             let! y = x.FirstAsync()
    //             return Ok y.AuthConnections
    //         | Error error ->
    //             return
    //                 error
    //                 |> sprintf "Repository error, failed to retrieve auth connections, user not found: %s"
    //                 |> Error
    //     })

    // let addAuthConnection userId (newAuthConnection: AuthConnection) =
    //     let filter = {| ``_id.UserId`` = userId |> UserId.raw |}
    //     let update = {| ``$push`` = {| AuthConnections = newAuthConnection |} |}

    //     collection.UpdateOneAsync(
    //         filter.ToBsonDocument(),
    //         update.ToBsonDocument()
    //     )
    //     |> TaskResult.simpleCatch (fun exn -> sprintf "Repository error, failed to add auth connection: %s" (exn.ToString()))
    //     |> TaskResult.map ignore