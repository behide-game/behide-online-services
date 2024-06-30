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

    let findUserById (userId: UserId) : Task<User option> =
        let filter = {| ``_id`` = userId |}

        filter.ToBsonDocument()
        |> BsonDocumentFilterDefinition
        |> collection.FindAsync
        |> Task.bind (fun users -> users.FirstOrDefaultAsync())
        |> Task.map Option.ofNull

    let findUserByNameIdentifier (nameIdentifier: string) : Task<User option> =
        let filter = {| ``AuthConnection.Fields.0`` = nameIdentifier |}

        filter.ToBsonDocument()
        |> BsonDocumentFilterDefinition
        |> collection.FindAsync
        |> Task.bind (fun users -> users.FirstOrDefaultAsync())
        |> Task.map Option.ofNull

    let addRefreshTokenHashToUser userId (newRefreshTokenHash: Api.Auth.RefreshTokenHash) =
        let filter = {| ``_id.UserId`` = userId |> UserId.raw |}
        let update = {| ``$push`` = {| RefreshTokenHashes = newRefreshTokenHash |} |}

        collection.UpdateOneAsync(
            filter.ToBsonDocument(),
            update.ToBsonDocument()
        )
        |> Task.map (fun res ->
            result {
                do! res.IsAcknowledged |> Result.requireTrue ()
                do! res.MatchedCount |> Result.requireEqualTo 1 ()
                do! res.ModifiedCount |> Result.requireEqualTo 1 ()
            }
        )

    let setRefreshTokenHashesOfUser userId (hashes: Api.Auth.RefreshTokenHash array) =
        let filter = {| ``_id`` = userId |}
        let update = {| ``$set`` = {| RefreshTokenHashes = hashes |} |}

        collection.UpdateOneAsync(
            filter.ToBsonDocument(),
            update.ToBsonDocument()
        )
        |> Task.map (fun res ->
            result {
                do! res.IsAcknowledged |> Result.requireTrue ()
                do! res.MatchedCount |> Result.requireEqualTo 1 ()
                do! res.ModifiedCount |> Result.requireEqualTo 1 ()
            }
        )