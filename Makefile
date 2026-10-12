# SourceSharp Host: the front door. `make setup` first (writes roots.props).
SHELL := /bin/bash
.SHELLFLAGS := -eo pipefail -c

DOTNET        ?= $(HOME)/.dotnet/dotnet
CONFIG        ?= Debug
MAPTOOLS_ROOT ?= ../source-sdk-map-tools
MAPTOOLS_REPO ?= https://github.com/muflub/source-sdk-sharp-map-tools.git
# dev only: `make mod-image-dev` packs a mod image from its bin/release/. Not a build root.
SOURCE_SHARP_ROOT ?= ../source-sdk-sharp

KUBECONFIG_FILE ?= $(CURDIR)/local-k3s.yaml
KUBECTL       ?= KUBECONFIG=$(KUBECONFIG_FILE) kubectl
# ISOLATED=1: a separate copy in tf2-descent-iso, beside tf2-descent and touching none of it
# (deploy/k8s/isolated: client UDP on 27215, fastdl on 5204).
ISOLATED      ?=
NAMESPACE     ?= $(if $(ISOLATED),tf2-descent-iso,tf2-descent)
REGISTRY      ?= localhost:30500
TAG           ?= $(shell git rev-parse --short HEAD)
IMAGE_TOOL    ?= podman
TIER          ?= fake
MOD_IMAGE     ?=
MOD_NAME      ?= tf2_descent
# The 64-bit libraries anonymous 244310 lacks (plan D-H11): a directory whose bin/linux64/ holds them.
# Default: the local Steam install of app 243750. Production: a licensed source (docs/ops.md).
ENGINE_FILES  ?= $(HOME)/.steam/steam/steamapps/common/Source SDK Base 2013 Multiplayer
STEAMWORKS_SDK ?= $(CURDIR)/deploy/steamworks

TEST_ENV      := MSBUILDDISABLENODEREUSE=1
SLN           := Host.slnx

.PHONY: setup build test check bench pack publish clean run local gateway fake \
        images image-service image-gateway image-engine image-fake \
        cluster-up cluster-down deploy undeploy admin logs mod-image-dev \
        bake live-h0 live-h4 live-h5 live-h6 live-h7 live-h9 live-s1 maptools-clean

setup:
	@if [ ! -d "$(MAPTOOLS_ROOT)/.git" ]; then git clone "$(MAPTOOLS_REPO)" "$(MAPTOOLS_ROOT)"; fi
	@root=$$(cd "$(MAPTOOLS_ROOT)" && pwd); \
	if [ -n "$$(git -C "$$root" status --porcelain)" ]; then \
	  echo "refusing: $$root is dirty (this repo never builds against uncommitted map tools)"; \
	  git -C "$$root" status --porcelain | head; exit 1; fi; \
	printf '<Project>\n  <PropertyGroup>\n    <MapToolsRoot>%s/</MapToolsRoot>\n  </PropertyGroup>\n</Project>\n' "$$root" > roots.props; \
	echo "MapToolsRoot = $$root at $$(git -C "$$root" log --oneline -1)"
	$(DOTNET) restore $(SLN) --nologo -v q

build:
	$(TEST_ENV) $(DOTNET) build $(SLN) --nologo -m:1 -c $(CONFIG) -v q
	@$(MAKE) --no-print-directory maptools-clean

test:
	$(TEST_ENV) $(DOTNET) test $(SLN) --nologo -m:1 -c $(CONFIG)
	@$(MAKE) --no-print-directory maptools-clean

check: build test

# The §3.1 measurement table of docs/plans/plan_build_patch.md, reproducible:
# builds the fixtures, prints the stream-level delta table, asserts the rulings
# (exit ≠ 0 when the measured basis moved). Standalone project, not in Host.slnx.
bench:
	DOTNET=$(DOTNET) $(TEST_ENV) $(DOTNET) run --project bench/Bench.csproj -c Release -v q --nologo

# We never write into a root: after any build its porcelain status is empty.
maptools-clean:
	@root=$$(sed -n 's#.*<MapToolsRoot>\(.*\)</MapToolsRoot>.*#\1#p' roots.props); \
	if [ -n "$$(git -C "$$root" status --porcelain)" ]; then echo "MapToolsRoot $$root is dirty after the build"; exit 1; fi

# PACK_VERSION=1.2.3 stamps the packages (CI passes the release tag); unset, the projects' default.
PACK_VERSION  ?=
pack:
	$(DOTNET) pack src/Host.Contracts/Host.Contracts.csproj -c Release -o bin/nupkg --nologo -v q $(if $(PACK_VERSION),-p:Version=$(PACK_VERSION))
	$(DOTNET) pack src/Host.Sdk/Host.Sdk.csproj -c Release -o bin/nupkg --nologo -v q $(if $(PACK_VERSION),-p:Version=$(PACK_VERSION))
	DOTNET=$(DOTNET) deploy/pack-check.sh

# The Linux build: linux-x64 publishes of the executables, tarred into bin/dist (CI uploads these).
publish:
	DOTNET=$(DOTNET) BUILD_VERSION=$(TAG) deploy/publish-linux.sh
	@$(MAKE) --no-print-directory maptools-clean

run:
	$(DOTNET) run --project src/TF2.Descent.Service

# D-H13: the real service as one local process; the game connects with -hostlocal $(LOCAL_DIR)/local-host.json
LOCAL_DIR ?= $(CURDIR)/bin/local
local:
	$(DOTNET) run --project src/TF2.Descent.Service -- --local "$(LOCAL_DIR)"
gateway:
	$(DOTNET) run --project src/Host.Gateway
fake:
	$(DOTNET) run --project src/Host.FakeGame

bake:
	$(DOTNET) run --project src/TF2.Descent.Service -- $@ $(ARGS)

# ---- images (podman or buildah; docker works too with IMAGE_TOOL=docker) ----
images: image-service image-gateway image-engine image-fake
# The service is this game's (tf2-descent-service); the gateway and the fake are generic (sourcesharp-*).
image-service: IMAGE_NAME = tf2-descent-service
image-fake: IMAGE_NAME = sourcesharp-$*
image-service image-fake: image-%:
	@root=$$(sed -n 's#.*<MapToolsRoot>\(.*\)</MapToolsRoot>.*#\1#p' roots.props); \
	$(IMAGE_TOOL) build -f deploy/Dockerfile.$* --build-context maptools="$$root" \
	  --build-arg BUILD_VERSION=$(TAG) -t $(REGISTRY)/$(IMAGE_NAME):$(TAG) .
	$(IMAGE_TOOL) push --tls-verify=false $(REGISTRY)/$(IMAGE_NAME):$(TAG)

# The gateway carries the Steam game server (plan_steam_auth S-1): STEAMWORKS_SDK is the Steamworks SDK's
# root (its redistributable_bin/linux64/libsteam_api.so goes in the image); by default the redistributables
# checked in under deploy/steamworks (deploy/steamworks/README.md).
image-gateway:
	@root=$$(sed -n 's#.*<MapToolsRoot>\(.*\)</MapToolsRoot>.*#\1#p' roots.props); \
	[ -f "$(STEAMWORKS_SDK)/redistributable_bin/linux64/libsteam_api.so" ] || { echo "STEAMWORKS_SDK=$(STEAMWORKS_SDK) has no redistributable_bin/linux64/libsteam_api.so (the Steamworks SDK root; docs/ops.md)"; exit 1; }; \
	$(IMAGE_TOOL) build -f deploy/Dockerfile.gateway --build-context maptools="$$root" \
	  --build-context steamworks="$(STEAMWORKS_SDK)" \
	  --build-arg BUILD_VERSION=$(TAG) -t $(REGISTRY)/sourcesharp-gateway:$(TAG) .
	$(IMAGE_TOOL) push --tls-verify=false $(REGISTRY)/sourcesharp-gateway:$(TAG)

image-engine:
	@root=$$(sed -n 's#.*<MapToolsRoot>\(.*\)</MapToolsRoot>.*#\1#p' roots.props); \
	[ -d "$(ENGINE_FILES)/bin/linux64" ] || { echo "ENGINE_FILES=$(ENGINE_FILES) has no bin/linux64 (docs/ops.md)"; exit 1; }; \
	$(IMAGE_TOOL) build -f deploy/Dockerfile.engine --build-context maptools="$$root" \
	  --build-context enginefiles="$(ENGINE_FILES)" \
	  --build-arg BUILD_VERSION=$(TAG) -t $(REGISTRY)/sourcesharp-engine:$(TAG) .
	$(IMAGE_TOOL) push --tls-verify=false $(REGISTRY)/sourcesharp-engine:$(TAG)

mod-image-dev:
	MOD_NAME=$(MOD_NAME) deploy/mod-image-dev.sh "$(SOURCE_SHARP_ROOT)" "$(REGISTRY)/tf2-descent-mod:dev"

# ---- cluster (local k3s through $(KUBECONFIG_FILE); never a cluster someone else uses) ----
cluster-up:
	deploy/cluster-up.sh "$(KUBECONFIG_FILE)" "$(NAMESPACE)" "$(MOD_IMAGE)" "$(TIER)"
cluster-down:
	$(KUBECTL) delete namespace $(NAMESPACE) --wait=true
OVERLAY       := deploy/k8s/$(if $(ISOLATED),isolated-$(if $(filter fake,$(TIER)),fake,real),$(if $(filter fake,$(TIER)),local-fake,local))
CONTENT_PATH  ?= $(HOME)/.steam/steam/steamapps/common/Team Fortress 2
# One apply: the manifests and, with MOD_IMAGE, the ConfigMap the service reads it from. A second
# step (`set env`, as before) rolled the service twice, and the first pod could create a hub with
# no mod image, which then boot-timed-out (measured on k3s, 2026-10-02 12:45).
deploy:
	@for img in tf2-descent-service sourcesharp-gateway sourcesharp-engine; do \
	  curl -sf -o /dev/null -H 'Accept: application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json' \
	    "http://$(REGISTRY)/v2/$$img/manifests/$(TAG)" \
	  || { echo "deploy: $(REGISTRY)/$$img:$(TAG) is not in the registry (run make images, or pass TAG=<a built tag>)" >&2; exit 1; }; done
	@set -e; { $(KUBECTL) kustomize $(OVERLAY) | sed -e "s#:latest#:$(TAG)#g" -e "s#__CONTENT_PATH__#$(CONTENT_PATH)#" -e "s#__MOD_IMAGE__#$(MOD_IMAGE)#"; \
	  if [ -n "$(MOD_IMAGE)" ]; then \
	    ref="$(MOD_IMAGE)"; reg=$${ref%%/*}; rest=$${ref#*/}; name=$${rest%%:*}; tag=$${rest##*:}; \
	    echo "---"; $(KUBECTL) -n $(NAMESPACE) create configmap tf2-descent-mod-image --dry-run=client -o yaml \
	      --from-literal=SOURCESHARP_SERVICE_Instances__ModImage__Registry="$$reg" --from-literal=SOURCESHARP_SERVICE_Instances__ModImage__Name="$$name" \
	      --from-literal=SOURCESHARP_SERVICE_Instances__ModImage__Tag="$$tag" --from-literal=SOURCESHARP_SERVICE_Mod__Name="$(MOD_NAME)"; fi; } \
	  | $(KUBECTL) apply -f -
	$(KUBECTL) -n $(NAMESPACE) rollout status statefulset/tf2-descent-service --timeout=180s
	$(KUBECTL) -n $(NAMESPACE) rollout status deploy/tf2-descent-gateway --timeout=180s
undeploy:
	$(KUBECTL) kustomize $(OVERLAY) | sed -e "s#__CONTENT_PATH__#$(CONTENT_PATH)#" | $(KUBECTL) delete --ignore-not-found --wait=true -f -
	$(KUBECTL) -n $(NAMESPACE) delete configmap tf2-descent-mod-image --ignore-not-found
	$(KUBECTL) -n $(NAMESPACE) wait --for=delete pod -l app=sourcesharp-game --timeout=90s || true
admin:
	$(KUBECTL) -n $(NAMESPACE) port-forward pod/tf2-descent-service-0 5000:5000
logs:
	$(KUBECTL) -n $(NAMESPACE) logs -f statefulset/tf2-descent-service

live-h0 live-h4 live-h5 live-h6 live-h7 live-h9 live-s1: live-%:
	TIER=$(TIER) KUBECONFIG=$(KUBECONFIG_FILE) NAMESPACE=$(NAMESPACE) live/$*.sh

clean:
	rm -rf bin
