let express = require("express"),
  async = require("async"),
  pg = require("pg"),
  path = require("path"),
  cookieParser = require("cookie-parser"),
  bodyParser = require("body-parser"),
  methodOverride = require("method-override"),
  app = express(),
  server = require("http").Server(app),
  io = require("socket.io")(server)

io.set("transports", ["polling"])

const port = process.env.PORT || 8888

io.sockets.on("connection", function (socket) {
  socket.emit("message", {text: "Welcome!"})

  socket.on("subscribe", function (data) {
    socket.join(data.channel)
  })
})

const pgHost = process.env.POSTGRES_HOST || "db"
const pgPort = process.env.POSTGRES_PORT || 5432
const pgUser = process.env.POSTGRES_USER || "postgres"
const pgPass = process.env.POSTGRES_PASSWORD || "postgres"
const pgDb = process.env.POSTGRES_DB || "postgres"

const connectionString =
  process.env.DATABASE_URL ||
  `postgres://${pgUser}:${pgPass}@${pgHost}:${pgPort}/${pgDb}`

const pool = new pg.Pool({
  connectionString: connectionString,
})

async.retry(
  {times: 1000, interval: 1000},
  function (callback) {
    pool.connect(function (err, client, done) {
      if (err) {
        console.error("Waiting for db")
      }
      if (client) {
        done()
      }
      callback(err, client)
    })
  },
  function (err) {
    if (err) {
      return console.error("Giving up")
    }
    console.log("Connected to db")
    getVotes()
  },
)

function getVotes() {
  pool.query("SELECT vote, COUNT(id) AS count FROM votes GROUP BY vote", [], function (err, result) {
    if (err) {
      console.error("Error performing query: " + err)
    } else {
      const votes = collectVotesFromResult(result)
      io.sockets.emit("scores", JSON.stringify(votes))
    }

    setTimeout(function () {
      getVotes()
    }, 1000)
  })
}

function collectVotesFromResult(result) {
  const votes = {a: 0, b: 0}

  result.rows.forEach(function (row) {
    votes[row.vote] = parseInt(row.count)
  })

  return votes
}

app.use(cookieParser())
app.use(bodyParser.urlencoded({ extended: true }))
app.use(bodyParser.json())
app.use(methodOverride("X-HTTP-Method-Override"))
app.use(function (req, res, next) {
  res.header("Access-Control-Allow-Origin", "*")
  res.header("Access-Control-Allow-Headers", "Origin, X-Requested-With, Content-Type, Accept")
  res.header("Access-Control-Allow-Methods", "PUT, GET, POST, DELETE, OPTIONS")
  next()
})

app.use(express.static(__dirname + "/views"))

app.get("/", function (req, res) {
  res.sendFile(path.resolve(__dirname + "/views/index.html"))
})

server.listen(port, function () {
  const actualPort = server.address().port
  console.log("App running on port " + actualPort)
})
