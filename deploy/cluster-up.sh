#!/bin/sh
# Bring up the local cluster's pieces for this repo: the registry, the namespace, and the
# mod image seed (plan H1a: `make cluster-up` seeds Instances.ModImage from MOD_IMAGE).
# Refuses any context that is not the local k3s this repo was given.
set -eu
kubeconfig=${1:?kubeconfig}; ns=${2:?namespace}; mod_image=${3:-}; tier=${4:-fake}
export KUBECONFIG="$kubeconfig"
server=$(kubectl config view --minify -o jsonpath='{.clusters[0].cluster.server}')
case "$server" in
  https://127.0.0.1:*|https://localhost:*) ;;
  *) echo "cluster-up: refusing $server: not a local cluster" >&2; exit 1 ;;
esac
kubectl apply -f "$(dirname "$0")/k8s/registry/registry.yaml"
kubectl -n descent-registry rollout status deploy/registry --timeout=120s
kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f -
# MOD_IMAGE is applied by `make deploy MOD_IMAGE=…` as the service's DESCENT_Instances__ModImage__* env.
echo "cluster-up: registry localhost:30500, namespace $ns, tier $tier${mod_image:+, mod image $mod_image}"
