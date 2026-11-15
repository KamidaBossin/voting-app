# Déploiement conteneurisé de la Voting App

Ce document décrit l'architecture, les choix techniques et les procédures d'exploitation pour le déploiement de l'application distribuée Voting App. Il couvre la conteneurisation individuelle des modules, l'assemblage sous Docker Compose et l'orchestration sur un cluster Docker Swarm.

---

## 1. Architecture applicative et réseau

L'application permet à une audience de voter entre deux options (Cats ou Dogs) et de visualiser les scores en direct. Elle repose sur cinq composants :

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

### Topologie et isolation des réseaux

Pour des raisons de sécurité, l'architecture interdit tout accès direct entre le service frontal de vote et la base de données relationnelle. Le réseau est découpé en deux segments :

- **front** (bridge en local, overlay sous Swarm) : regroupe `vote`, `redis` et `worker`. Le vote enregistre son message dans Redis sans jamais connaître la base de données.
- **back** (bridge en local, overlay sous Swarm) : regroupe `worker`, `db` et `result`. Le service de consultation interroge PostgreSQL sans avoir d'accès à Redis.
- **worker** : unique composant connecté simultanément aux deux réseaux. Il assure la passerelle applicative en dépilant les votes de la file d'attente pour les persister en base.

---

## 2. Structure du dépôt

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

## 3. Prérequis système

- Docker Engine version 24 ou supérieure.
- Docker Compose plugin version 2 ou supérieure.
- Un terminal avec support de Make (ou exécution directe des commandes docker).
- Ports 8080 et 8888 libres sur la machine hôte.

---

## 4. Déploiement local avec Docker Compose

### Initialisation de la configuration

Copier le fichier modèle pour charger les variables d'environnement :

```bash
cp .env.example .env
```

Le fichier `.env` définit les ports d'écoute, les identifiants PostgreSQL, le texte des deux options soumises au vote et la version des images à publier (`IMAGE_TAG`).

### Lancement de la stack

Pour compiler les images et démarrer l'ensemble des conteneurs en arrière-plan :

```bash
make up
# ou directement : docker compose up -d
```

Les images sont étiquetées avec la version déclarée dans `IMAGE_TAG` (fichier `.env`), soit `amidaemmanuelle/voting-vote:1.0`, `amidaemmanuelle/voting-worker:1.0` et `amidaemmanuelle/voting-result:1.0`. Les conteneurs sont reconstruits au premier lancement.

### Vérification du statut et des sondes de santé

Docker Compose applique des conditions de dépendance `service_healthy`. Les services `redis` et `db` démarrent d'abord. Une fois leurs sondes positives, `vote`, `worker` et `result` sont instanciés à leur tour.

Afficher l'état d'exécution :

```bash
make ps
# ou : docker compose ps
```

La colonne `STATUS` doit afficher `Up ... (healthy)` pour les cinq services : `db`, `redis`, `vote`, `worker` et `result`.

Le `worker` n'expose aucune interface de vote, il écoute donc un point de santé dédié sur le port 8081. La réponse indique l'état de ses deux connexions :

```bash
docker compose exec worker wget -qO- http://127.0.0.1:8081/health
```

```json
{"worker":"up","redis":"ok","database":"ok"}
```

Pendant que la base est arrêtée, le point de santé renvoie un code `503` et le champ `database` passe à `ko`. Le worker se reconnecte tout seul, le vote en attente n'est pas perdu.

### Accès aux interfaces

- Interface de vote : [http://localhost:8080](http://localhost:8080)
- Visualisation des résultats en direct : [http://localhost:8888](http://localhost:8888)

### Test de persistance des données

1. Se rendre sur `http://localhost:8080` et émettre plusieurs votes pour l'option Cats ou Dogs (ou via la commande `make test-vote`).
2. Constater la mise à jour instantanée des jauges sur `http://localhost:8888`.
3. Arrêter l'application en détruisant les conteneurs :
   ```bash
   make down
   # ou : docker compose down
   ```
4. Relancer la stack :
   ```bash
   make up
   ```
5. Recharger `http://localhost:8888` : le décompte des votes est préservé intact. Le volume nommé `pgdata` a maintenu les données écrites dans `/var/lib/postgresql/data`, tandis que `redisdata` a conservé le journal d'opérations AOF dans `/data`.
6. Pour purger définitivement les volumes lors d'une remise à zéro complète :
   ```bash
   make down-v
   ```

---

## 5. Déploiement sur cluster Docker Swarm

Le déploiement sur cluster respecte les contraintes propres à l'orchestrateur : les directives `build:` sont retirées au profit d'images versionnées, les réseaux passent en pilote `overlay`, et des contraintes de placement fixent les bases de données sur les hôtes portant le stockage persistant.

### A. Construction et publication des images

Swarm ne construisant pas d'images au déploiement, celles-ci doivent être préalablement compilées puis publiées sur le registre Docker Hub :

```bash
docker compose build
docker login
make push
# pousse amidaemmanuelle/voting-vote, voting-worker et voting-result
```

Le préfixe des dépôts correspond au compte Docker Hub du groupe. La version poussée est lue dans `IMAGE_TAG` (fichier `.env`), ce qui évite de déployer un tag `:latest` non figé. Les trois dépôts sont publics et référencent la version `1.0` :

| Dépôt | Version | Taille sur le registre |
|---|---|---|
| `amidaemmanuelle/voting-vote` | 1.0 | 18,9 Mo |
| `amidaemmanuelle/voting-worker` | 1.0 | 37,8 Mo |
| `amidaemmanuelle/voting-result` | 1.0 | 64 Mo |

### B. Constitution d'un cluster à 3 nœuds (1 manager, 2 workers)

Pour reproduire fidèlement l'environnement de production demandé (1 nœud manager et 2 nœuds workers), deux méthodes sont utilisables.

#### Méthode recommandée : Docker in Docker (DinD)

Cette méthode fonctionne directement sur toute machine équipée de Docker sans requérir d'hyperviseur externe :

```bash
# 1. Création du réseau dédié au cluster
docker network create swarm-lab

# 2. Lancement des trois nœuds
docker run -d --privileged --name manager1 --hostname manager1 --network swarm-lab \
  -p 8080:8080 -p 8888:8888 docker:29-dind

docker run -d --privileged --name worker1 --hostname worker1 --network swarm-lab docker:29-dind
docker run -d --privileged --name worker2 --hostname worker2 --network swarm-lab docker:29-dind

# 3. Initialisation du Swarm sur le manager
docker exec manager1 docker swarm init

# 4. Récupération du jeton d'adhésion pour les workers
TOKEN=$(docker exec manager1 docker swarm join-token -q worker)

# 5. Ralliement des workers au manager
docker exec worker1 docker swarm join --token $TOKEN manager1:2377
docker exec worker2 docker swarm join --token $TOKEN manager1:2377

# 6. Vérification de la topologie du cluster
docker exec manager1 docker node ls
```

La sortie affiche trois nœuds avec le statut `Ready` / `Active`, dont `manager1` portant le statut `Leader`.

#### Méthode alternative : Machines virtuelles avec Vagrant

Si l'on dispose d'un hyperviseur (VirtualBox ou VMware), un fichier `Vagrantfile` paramètre trois machines virtuelles Ubuntu 24.04 avec adresses IP privées (192.168.56.100 pour manager1, .101 pour worker1, .102 pour worker2). Le démon Docker y est installé automatiquement et piloté à distance via les contextes Docker (`docker context create manager1 --docker host=tcp://192.168.56.100:2375`).

### C. Déploiement de la stack sur le cluster

Sur le nœud manager (ou en local si le contexte pointe sur le cluster) :

```bash
make stack-deploy
```

Le descripteur fait référence à des variables (`${POSTGRES_USER}`, `${IMAGE_TAG}`...). La commande `docker stack deploy` ne sait pas lire le fichier `.env`, il faut donc exporter les variables avant le déploiement. C'est exactement ce que fait la cible `make stack-deploy` :

```bash
set -a; . ./.env; set +a; docker stack deploy -c docker-stack.yml voting
```

Vérification des services et de leurs répliques :

```bash
docker stack services voting
```

Sortie attendue :
```
ID             NAME            MODE         REPLICAS   IMAGE                                PORTS
...            voting_db       replicated   1/1        postgres:17-alpine
...            voting_redis    replicated   1/1        redis:8-alpine
...            voting_result   replicated   2/2        amidaemmanuelle/voting-result:1.0    *:8888->8888/tcp
...            voting_vote     replicated   2/2        amidaemmanuelle/voting-vote:1.0      *:8080->8080/tcp
...            voting_worker   replicated   1/1        amidaemmanuelle/voting-worker:1.0
```

Affichage de la répartition des tâches sur les nœuds :

```bash
docker stack ps voting
```

### D. Vérification de la haute disponibilité

1. Les services `vote` et `result` tournent avec `replicas: 2`. Le routing mesh Swarm achemine le trafic arrivé sur le port 8080 ou 8888 vers n'importe quel conteneur actif, quel que soit le nœud sollicité.
2. Pour tester la tolérance aux pannes, drainer ou arrêter l'un des workers :
   ```bash
   docker node update --availability drain worker1
   ```
3. L'exécution de `docker stack ps voting` confirme que les tâches précédemment situées sur `worker1` sont aussitôt arrêtées et relancées sur les nœuds restants. L'interface web reste continuellement accessible sans interruption.
4. Remise en service :
   ```bash
   docker node update --availability active worker1
   ```

---

## 6. Journal des modifications du code source

Conformément aux consignes du sujet, les modifications ont été effectuées de manière ciblée sans altération de la logique métier. Le tableau ci-dessous explicite chaque changement :

| Fichier | Modification effectuée | Justification technique |
|---|---|---|
| `vote/app.py` | Remplacement de `Redis(host="localhost", ...)` par les variables `REDIS_HOST` (repli `redis`) et `REDIS_PORT` (repli `6379`). | Dans un conteneur, `localhost` référence le conteneur lui-même. La communication inter-conteneurs impose de joindre le service par son alias réseau. |
| `vote/app.py` | Ajout de la variable `PORT` et passage de `debug=True` à `debug=False`. | Le mode debug de Werkzeug instancie deux processus et compromet la transmission des signaux système d'arrêt propre (SIGTERM). |
| `vote/requirements.txt` | Épinglage de `Flask==3.0.3`, `redis==5.0.4` et ajout de `gunicorn==22.0.0`. | Garantie de reproductibilité des builds et utilisation d'un serveur applicatif robuste pour la production. |
| `worker/Program.cs` | Paramétrage des chaînes de connexion à PostgreSQL et à Redis via variables d'environnement. | Suppression des identifiants et hôtes en dur afin de s'adapter à la configuration injectée par Compose ou Swarm. |
| `worker/Program.cs` | Déplacement de l'appel `GetIp(hostname)` à l'intérieur de la boucle de tentative de connexion. | En l'absence de ce déplacement, un retard d'une seconde dans la résolution DNS de Redis provoque une exception non interceptée et l'arrêt brutal du conteneur. |
| `worker/Worker.csproj` | Migration du runtime cible vers `net8.0` et actualisation des bibliothèques (`Npgsql` 8.0.3, `StackExchange.Redis` 2.8.0). | .NET Core 7 n'est plus maintenu depuis mai 2024. Le passage à la version LTS 8.0 assure le support de sécurité. |
| `result/server.js` | Lecture de `PORT` et de la chaîne de connexion PostgreSQL via `DATABASE_URL` / `POSTGRES_*`. | Permet de connecter l'application au service `db` du réseau Docker au lieu de cibler un `localhost` inaccessible. |
| `result/server.js` | Remplacement de la connexion par client figé par `pool.query`. | Si la base de données redémarre, les requêtes suivantes acquièrent une nouvelle connexion saine depuis le pool au lieu d'accumuler des erreurs fatales. |
| `worker/Program.cs` | Suppression de la commande `keepAliveCommand` créée une seule fois au démarrage. | Une commande SQL appartient à la connexion qui l'a créée. Après une reconnexion, elle pointait sur l'ancienne connexion fermée et levait une exception `57P01` qui arrêtait le processus. La commande est désormais recréée à chaque appel. |
| `worker/Program.cs` | Ajout d'un point de santé HTTP `/health` sur le port 8081, servi par un thread de fond. | Le worker n'a pas d'interface web. Ce point de santé permet au conteneur de disposer d'une sonde réelle et indique au superviseur l'état des connexions Redis et PostgreSQL. Le thread est protégé par un try/catch : une panne de la sonde ne peut pas tuer le worker. |
| `worker/Program.cs` | Remise en file du vote dépilé lorsque la connexion PostgreSQL doit être renouvelée. | Le vote dépilé depuis Redis était perdu si la base était indisponible. Il est désormais repoussé en fin de file avec `RPUSH` puis traité dès la reprise de la base. |
| `worker/Program.cs` | Reconnexion automatique sur erreur de connexion et protection de l'appel `LPOP` par un try/catch. | Avant, une coupure de Redis ou de PostgreSQL suffisait à terminer le conteneur avec le code 1. Le worker absorbe l'incident, journalise l'erreur et attend la reprise du service. |
| `worker/Dockerfile` | Ajout de la directive `EXPOSE 8081`. | Le port de santé est documenté dans l'image, comme les ports applicatifs des deux autres services. |
| `result/server.js`, `result/views/` | Les libellés des deux options sont lus dans `OPTION_A` et `OPTION_B` puis transmis au client. | Les libellés étaient figés dans la page de résultats alors que la page de vote les prenait dans l'environnement. Les deux écrans lisent désormais les mêmes variables. |
| `docker-compose.yml`, `docker-stack.yml` | Images versionnées (`amidaemmanuelle/voting-*:1.0`) au lieu du tag `:latest`. | Le tag `:latest` est flottant et rend le déploiement Swarm irreproductible : le cluster peut tirer une image différente de celle qui a été testée en local. |
| `docker-compose.yml`, `docker-stack.yml`, `Makefile` | Dépôts renommés `amidaemmanuelle/voting-*`, le préfixe initial ne correspondant à aucun compte Docker Hub. | Swarm ne construit pas d'images : il les tire du registre. Avec un préfixe inexistant, les tâches restaient en attente et le cluster ne démarrait pas. Les trois dépôts sont désormais publics. |
| `docker-compose.yml` | Ajout d'une sonde de santé sur le service `worker`. | Les cinq services de la stack disposent maintenant d'un contrôle de disponibilité exploitable par `docker compose ps`. |
| `Makefile` | Ajout de la variable `IMAGE_TAG` et export du fichier `.env` dans la cible `stack-deploy`. | `docker stack deploy` n'a pas d'option `--env-file` et n'interpole rien depuis `.env`. Les variables doivent être exportées dans l'environnement avant la commande, sinon la stack repart sur les valeurs par défaut. |

---

## 7. Justification des choix de conteneurisation

### Optimisation des images et build multi-étapes
Le module `worker` (.NET) fait appel à un Dockerfile multi-stage. L'étape de compilation s'appuie sur le SDK volumineux (~750 Mo) pour restaurer les dépendances et publier les binaires. L'image finale ne conserve que le runtime Alpine et le dossier de publication.

| Image | Sur le disque local | Téléchargée (compressée) |
|---|---|---|
| `voting-vote` | 83 Mo | 19,8 Mo |
| `voting-worker` | 128 Mo | 39,6 Mo |
| `voting-result` | 269 Mo | 67,1 Mo |

`docker image ls` affiche deux colonnes de taille : la première correspond à l'espace occupé sur le disque local, la seconde à ce qui transite réellement lors d'un `docker pull`. C'est la seconde qui pèse sur la bande passante du cluster. Le registre annonce des valeurs très proches (18,9 Mo pour l'image vote), l'écart venant de la façon dont il additionne les couches compressées.

### Sécurité et principe du moindre privilège
Aucun service applicatif ne s'exécute sous l'utilisateur `root` :
- `vote` : utilisateur créé `appuser` (UID 1000).
- `worker` : utilisateur créé `appuser` (UID 1000).
- `result` : utilisateur interne `node` de l'image de base.

La directive `COPY --chown` affecte directement la propriété des fichiers lors de la copie sans générer de couche intermédiaire de modification des droits.

### Gestion des sondes et robustesse
Les healthchecks utilisent les outils déjà présents dans chaque image :
- PostgreSQL : `pg_isready` teste réellement l'acceptation des connexions SQL.
- Redis : `redis-cli ping` valide la réponse du moteur mémoire.
- Applications web : requêtes directes exécutées par l'interpréteur (Python `urllib` et Node `fetch`) pour éviter d'installer un binaire externe comme `curl` dans des images Alpine minimales.
- Worker : appel à `/health` sur le port 8081, résolu avec le `wget` de BusyBox déjà installé dans l'image Alpine du runtime .NET.

Le point de santé du worker va plus loin qu'un simple test de processus vivant : il publie l'état de ses connexions. Un worker isolé de sa base passe en `unhealthy` alors que le processus tourne toujours.

### Redémarrage de la base de données
Quand PostgreSQL s'arrête puis redémarre, le worker survit. Il détecte la coupure, remet en file le vote qu'il venait de dépiler, attend la reprise de la base et reprend son traitement comme si de rien n'était. La sonde `/health` repasse en `503` pendant la coupure puis redevient verte, sans intervention de l'opérateur.
