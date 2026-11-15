# Version des images publiees sur Docker Hub.
# Source de verite : la variable IMAGE_TAG du fichier .env.
IMAGE_TAG ?= $(or $(shell sed -n 's/^IMAGE_TAG=//p' .env 2>/dev/null | head -n 1),1.0)

.PHONY: help build up down down-v logs ps test-vote push stack-deploy stack-rm stack-ps stack-services

help:
	@echo "Commandes disponibles :"
	@echo "  make build          Construire les images Docker de l'application"
	@echo "  make up             Demarrer la stack Compose locale en arriere-plan"
	@echo "  make down           Arreter la stack Compose (conserve les volumes)"
	@echo "  make down-v         Arreter la stack Compose et supprimer les volumes"
	@echo "  make logs           Suivre les journaux de l'ensemble des services"
	@echo "  make ps             Afficher l'etat des conteneurs et de leurs sondes"
	@echo "  make test-vote      Envoyer un vote de test (Cats) via curl"
	@echo "  make push           Publier les images versionnees sur Docker Hub"
	@echo "  make stack-deploy   Deployer la stack sur le cluster Docker Swarm"
	@echo "  make stack-rm       Supprimer la stack du cluster Docker Swarm"
	@echo "  make stack-ps       Lister les taches Swarm et leur repartition"
	@echo "  make stack-services Afficher l'etat des services Swarm et leurs repliques"
	@echo ""
	@echo "Version des images : $(IMAGE_TAG)"

build:
	docker compose build

up:
	docker compose up -d

down:
	docker compose down

down-v:
	docker compose down -v

logs:
	docker compose logs -f

ps:
	docker compose ps

test-vote:
	curl -s -X POST -d "vote=a" http://localhost:8080/ > /dev/null && echo "Vote 'a' enregistre."

push:
	docker push amidaemmanuelle/voting-vote:$(IMAGE_TAG)
	docker push amidaemmanuelle/voting-worker:$(IMAGE_TAG)
	docker push amidaemmanuelle/voting-result:$(IMAGE_TAG)

stack-deploy:
	@set -a; [ -f ./.env ] && . ./.env; set +a; docker stack deploy -c docker-stack.yml voting

stack-rm:
	docker stack rm voting

stack-ps:
	docker stack ps voting

stack-services:
	docker stack services voting
