# Deploiement conteneurise de la Voting App

Ce document decrit l'architecture, les choix techniques et les procedures d'exploitation pour le deploiement de l'application distribuee Voting App. Il couvre la conteneurisation individuelle des modules, l'assemblage sous Docker Compose et l'orchestration sur un cluster Docker Swarm.

---

## 1. Architecture applicative et reseau

L'application permet a une audience de voter entre deux options (Cats ou Dogs) et de visualiser les scores en direct. Elle repose sur cinq composants :

```
[Navigateur Web]
       |
       v (port 8080)
   ( vote ) -------- RUSH "votes" ------> [ Redis 8 ]
  (Python 3.13)                                |
                                               v (LPOP)
                                        ( worker )
                                       (.NET 8 C#)
                                               |
                                               v (INSERT / UPDATE)
   ( result ) <------ SELECT (1s) ------- [ PostgreSQL 17 ]
 (Node 24 / IO)       (polling)
       |
       v (port 8888)
[Navigateur Web]
```

### Topologie et isolation des reseaux

Pour des raisons de securite, l'architecture interdit tout acces direct entre le service frontal de vote et la base de donnees relationnelle. Le reseau est decoupe en deux segments :

- **front** (bridge en local, overlay sous Swarm) : regroupe `vote`, `redis` et `worker`. Le vote enregistre son message dans Redis sans jamais connaitre la base de donnees.
- **back** (bridge en local, overlay sous Swarm) : regroupe `worker`, `db` et `result`. Le service de consultation interroge PostgreSQL sans avoir d'acces a Redis.
- **worker** : unique composant connecte simultanement aux deux reseaux. Il assure la passerelle applicative en depilant les votes de la file d'attente pour les persister en base.

---

## 2. Structure du depot

```
voting-app/
|-- Makefile               # Automatisation des commandes Compose et Swarm
|-- README.md              # Documentation technique d'exploitation
|-- docker-compose.yml     # Descripteur d'assemblage local
|-- docker-stack.yml       # Descripteur d'orchestration pour cluster Swarm
|-- .env.example           # Gabarit des variables d'environnement
|-- .gitignore             # Exclusion des secrets, caches et binaires
|-- vote/                  # Service web de vote (Python 3.13 / Flask / Gunicorn)
|   |-- Dockerfile
|   |-- .dockerignore
|   |-- app.py
|   |-- requirements.txt
|   |-- static/
|   `-- templates/
|-- worker/                # Consommateur et processeur de votes (.NET 8 C#)
|   |-- Dockerfile
|   |-- .dockerignore
|   |-- Program.cs
|   `-- Worker.csproj
`-- result/                # Service web de visualisation (Node.js 24 / Express / Socket.io)
    |-- Dockerfile
    |-- .dockerignore
    |-- server.js
    |-- package.json
    |-- package-lock.json
    `-- views/
```

---

## 3. Prerequis systeme

- Docker Engine version 24 ou superieure.
- Docker Compose plugin version 2 ou superieure.
- Un terminal avec support de Make (ou execution directe des commandes docker).
- Ports 8080 et 8888 libres sur la machine hote.

---

## 4. Deploiement local avec Docker Compose

### Initialisation de la configuration

Copier le fichier modele pour charger les variables d'environnement :

```bash
cp .env.example .env
```

Le fichier `.env` definit les ports d'ecoute, les identifiants PostgreSQL et le texte des deux options soumises au vote.

### Lancement de la stack

Pour compiler les images et demarrer l'ensemble des conteneurs en arriere-plan :

```bash
make up
# ou directement : docker compose up -d
```

### Verification du statut et des sondes de sante

Docker Compose applique des conditions de dependance `service_healthy`. Les services `redis` et `db` demarrent d'abord. Une fois leurs sondes positives, `vote`, `worker` et `result` sont instancies a leur tour.

Afficher l'etat d'execution :

```bash
make ps
# ou : docker compose ps
```

La colonne `STATUS` doit afficher `Up ... (healthy)` pour `db`, `redis`, `vote` et `result`.

### Acces aux interfaces

- Interface de vote : [http://localhost:8080](http://localhost:8080)
- Visualisation des resultats en direct : [http://localhost:8888](http://localhost:8888)

### Test de persistance des donnees

1. Se rendre sur `http://localhost:8080` et emettre plusieurs votes pour l'option Cats ou Dogs (ou via la commande `make test-vote`).
2. Constater la mise a jour instantanee des jauges sur `http://localhost:8888`.
3. Arreter l'application en detruisant les conteneurs :
   ```bash
   make down
   # ou : docker compose down
   ```
4. Relancer la stack :
   ```bash
   make up
   ```
5. Recharger `http://localhost:8888` : le decompte des votes est preserve intact. Le volume nomme `pgdata` a maintenu les donnees ecrites dans `/var/lib/postgresql/data`, tandis que `redisdata` a conserve le journal d'operations AOF dans `/data`.
6. Pour purger definitivement les volumes lors d'une remise a zero complete :
   ```bash
   make down-v
   ```

---

## 5. Deploiement sur cluster Docker Swarm

Le deploiement sur cluster respecte les contraintes propres a l'orchestrateur : les directives `build:` sont retirees au profit d'images versionnees, les reseaux passent en pilote `overlay`, et des contraintes de placement fixent les bases de donnees sur les hotes portant le stockage persistant.

### A. Construction et publication des images

Swarm ne construisant pas d'images au deploiement, celles-ci doivent etre prealablement compilees puis publiees sur le registre Docker Hub :

```bash
docker compose build
docker login
make push
# pousse kamidabossin/voting-vote, voting-worker et voting-result
```

### B. Constitution d'un cluster a 3 noeuds (1 manager, 2 workers)

Pour reproduire fidelement l'environnement de production demande (1 noeud manager et 2 noeuds workers), deux methodes sont utilisables.

#### Methode recommandee : Docker in Docker (DinD)

Cette methode fonctionne directement sur toute machine equipee de Docker sans requerir d'hyperviseur externe :

```bash
# 1. Creation du reseau dedie au cluster
docker network create swarm-lab

# 2. Lancement des trois noeuds
docker run -d --privileged --name manager1 --hostname manager1 --network swarm-lab \
  -p 8080:8080 -p 8888:8888 docker:29-dind

docker run -d --privileged --name worker1 --hostname worker1 --network swarm-lab docker:29-dind
docker run -d --privileged --name worker2 --hostname worker2 --network swarm-lab docker:29-dind

# 3. Initialisation du Swarm sur le manager
docker exec manager1 docker swarm init

# 4. Recuperation du jeton d'adhesion pour les workers
TOKEN=$(docker exec manager1 docker swarm join-token -q worker)

# 5. Ralliement des workers au manager
docker exec worker1 docker swarm join --token $TOKEN manager1:2377
docker exec worker2 docker swarm join --token $TOKEN manager1:2377

# 6. Verification de la topologie du cluster
docker exec manager1 docker node ls
```

La sortie affiche trois noeuds avec le statut `Ready` / `Active`, dont `manager1` portant le statut `Leader`.

#### Methode alternative : Machines virtuelles avec Vagrant

Si l'on dispose d'un hyperviseur (VirtualBox ou VMware), un fichier `Vagrantfile` parametre trois machines virtuelles Ubuntu 24.04 avec adresses IP privees (192.168.56.100 pour manager1, .101 pour worker1, .102 pour worker2). Le daemon Docker y est installe automatiquement et pilote a distance via les contextes Docker (`docker context create manager1 --docker host=tcp://192.168.56.100:2375`).

### C. Deploiement de la stack sur le cluster

Sur le noeud manager (ou en local si le contexte pointe sur le cluster) :

```bash
docker stack deploy -c docker-stack.yml voting
```

Verification des services et de leurs repliques :

```bash
docker stack services voting
```

Sortie attendue :
```
ID             NAME            MODE         REPLICAS   IMAGE                               PORTS
...            voting_db       replicated   1/1        postgres:17-alpine                  
...            voting_redis    replicated   1/1        redis:8-alpine                      
...            voting_result   replicated   2/2        kamidabossin/voting-result:latest   *:8888->8888/tcp
...            voting_vote     replicated   2/2        kamidabossin/voting-vote:latest     *:8080->8080/tcp
...            voting_worker   replicated   1/1        kamidabossin/voting-worker:latest   
```

Affichage de la repartition des taches sur les noeuds :

```bash
docker stack ps voting
```

### D. Verification de la haute disponibilite

1. Les services `vote` et `result` tournent avec `replicas: 2`. Le routing mesh Swarm achemine le trafic arrive sur le port 8080 ou 8888 vers n'importe quel conteneur actif, quel que soit le noeud sollicite.
2. Pour tester la tolerance aux pannes, drainer ou arreter l'un des workers :
   ```bash
   docker node update --availability drain worker1
   ```
3. L'execution de `docker stack ps voting` confirme que les taches precedemment situees sur `worker1` sont aussitot arretees et relancees sur les noeuds restants. L'interface web reste continuellement accessible sans interruption.
4. Remise en service :
   ```bash
   docker node update --availability active worker1
   ```

---

## 6. Journal des modifications du code source

Conformement aux consignes du sujet, les modifications ont ete effectuees de maniere ciblee sans alteration de la logique metier. Le tableau ci-dessous explicite chaque changement :

| Fichier | Modification effectuee | Justification technique |
|---|---|---|
| `vote/app.py` | Remplacement de `Redis(host="localhost", ...)` par les variables `REDIS_HOST` (repli `redis`) et `REDIS_PORT` (repli `6379`). | Dans un conteneur, `localhost` reference le conteneur lui-meme. La communication inter-conteneurs impose de joindre le service par son alias reseau. |
| `vote/app.py` | Ajout de la variable `PORT` et passage de `debug=True` a `debug=False`. | Le mode debug de Werkzeug instancie deux processus et compromet la transmission des signaux systeme d'arret propre (SIGTERM). |
| `vote/requirements.txt` | Epinglage de `Flask==3.0.3`, `redis==5.0.4` et ajout de `gunicorn==22.0.0`. | Garantie de reproductibilite des builds et utilisation d'un serveur applicatif robuste pour la production. |
| `worker/Program.cs` | Parametrage des chaines de connexion a PostgreSQL et a Redis via variables d'environnement. | Suppression des identifiants et hotes en dur afin de s'adapter a la configuration injectee par Compose ou Swarm. |
| `worker/Program.cs` | Deplacement de l'appel `GetIp(hostname)` a l'interieur de la boucle de tentative de connexion. | En l'absence de ce deplacement, un retard d'une seconde dans la resolution DNS de Redis provoque une exception non interceptee et l'arret brutal du conteneur. |
| `worker/Worker.csproj` | Migration du runtime cible vers `net8.0` et actualisation des bibliotheques (`Npgsql` 8.0.3, `StackExchange.Redis` 2.8.0). | .NET Core 7 n'est plus maintenu depuis mai 2024. Le passage a la version LTS 8.0 assure le support de securite. |
| `result/server.js` | Lecture de `PORT` et de la chaine de connexion PostgreSQL via `DATABASE_URL` / `POSTGRES_*`. | Permet de connecter l'application au service `db` du reseau Docker au lieu de cibler un `localhost` inaccessible. |
| `result/server.js` | Remplacement de la connexion par client fige par `pool.query`. | Si la base de donnees redemarre, les requetes suivantes acquierent une nouvelle connexion saine depuis le pool au lieu d'accumuler des erreurs fatales. |

---

## 7. Justification des choix de conteneurisation

### Optimisation des images et build multi-etapes
Le module `worker` (.NET) fait appel a un Dockerfile multi-stage. L'etape de compilation s'appuie sur le SDK volumineux (~750 Mo) pour restaurer les dependances et publier les binaires. L'image finale ne conserve que le runtime Alpine et le dossier de publication, descendant la taille finale a 39,6 Mo.

### Securite et principe du moindre privilege
Aucun service applicatif ne s'execute sous l'utilisateur `root` :
- `vote` : utilisateur cree `appuser` (UID 1000).
- `worker` : utilisateur cree `appuser` (UID 1000).
- `result` : utilisateur interne `node` de l'image de base.

La directive `COPY --chown` affecte directement la propriete des fichiers lors de la copie sans generer de couche intermediaire de modification des droits.

### Gestion des sondes et robustesse
Les healthchecks utilisent les outils deja presents dans chaque image :
- PostgreSQL : `pg_isready` teste reellement l'acceptation des connexions SQL.
- Redis : `redis-cli ping` valide la reponse du moteur memoire.
- Applications web : requetes directes executees par l'interpreteur (Python `urllib` et Node `fetch`) pour eviter d'installer un binaire externe comme `curl` dans des images Alpine minimales.
