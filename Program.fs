module GiraffeJob.App

open System
open System.IO
open System.Collections.Generic
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Giraffe.Razor
open GiraffeJob.Models
open Microsoft.Azure.WebJobs
open Giraffe.HttpStatusCodeHandlers.Successful

type Message = { Text : string; Date : DateTime }

module WebJobs =
    open Microsoft.WindowsAzure.Storage
    open Microsoft.WindowsAzure.Storage.Queue
    open Newtonsoft.Json
    let start() =
        let host =
            let config =
                JobHostConfiguration(
                    DashboardConnectionString = "UseDevelopmentStorage=true",
                    StorageConnectionString = "UseDevelopmentStorage=true")
            config.UseDevelopmentSettings()
            new JobHost(config)
        host.Start()

    let post =
        let q =
            let q = CloudStorageAccount.DevelopmentStorageAccount.CreateCloudQueueClient()
            q.GetQueueReference "testqueue"
        fun message ->
            { Text = message; Date = DateTime.UtcNow }
            |> JsonConvert.SerializeObject
            |> CloudQueueMessage
            |> q.AddMessageAsync

let mutable lastMessage = None

let QueueJob([<QueueTrigger "TestQueue">] message : Message) =
    lastMessage <- Some message

// ---------------------------------
// Web app
// ---------------------------------

type PostRequest = { Message : string }

let webApp =
    choose [
        GET >=>
            choose [
                route "/" >=>
                    fun handler ctx ->
                        razorHtmlView
                            "Index"
                            { Text =
                                lastMessage
                                |> Option.map(fun m -> sprintf "On %O I said '%s'!" m.Date m.Text)
                                |> Option.defaultValue "Hello world, from Giraffe!" }
                            handler
                            ctx                            
            ]
        POST >=>
            fun handler ctx -> task {
                let! message = ctx.BindModelAsync<PostRequest>()
                do! WebJobs.post message.Message
                return! text "Sent!" handler ctx }
        setStatusCode 404 >=> text "Not Found" ]

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

// ---------------------------------
// Config and Main
// ---------------------------------

let configureCors (builder : CorsPolicyBuilder) =
    builder.WithOrigins("http://localhost:8080").AllowAnyMethod().AllowAnyHeader() |> ignore

let configureApp (app : IApplicationBuilder) =
    app.UseCors(configureCors)
       .UseGiraffeErrorHandler(errorHandler)
       .UseStaticFiles()
       .UseGiraffe(webApp)

let configureServices (services : IServiceCollection) =
    let sp  = services.BuildServiceProvider()
    let env = sp.GetService<IHostingEnvironment>()
    let viewsFolderPath = Path.Combine(env.ContentRootPath, "Views")
    services.AddRazorEngine viewsFolderPath |> ignore
    services.AddCors() |> ignore

let configureLogging (builder : ILoggingBuilder) =
    let filter (l : LogLevel) = l.Equals LogLevel.Error
    builder.AddFilter(filter).AddConsole().AddDebug() |> ignore

[<EntryPoint>]
let main argv =
    WebJobs.start()

    let contentRoot = Directory.GetCurrentDirectory()
    let webRoot     = Path.Combine(contentRoot, "WebRoot")
    WebHostBuilder()
        .UseKestrel()
        .UseContentRoot(contentRoot)
        .UseIISIntegration()
        .UseWebRoot(webRoot)
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureServices(configureServices)
        .ConfigureLogging(configureLogging)
        .Build()
        .Run()
    0