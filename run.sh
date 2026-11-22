#!/bin/sh
# Starts one of the executables in the image. Each Render service calls this with its own arguments.
#   run.sh web
#   run.sh worker cefi|defi|core
#   run.sh jobs check
set -e
case "$1" in
  web)    cd /app/web    && exec dotnet KatalepsisFT.Web.dll ;;
  worker) cd /app/worker && exec dotnet KatalepsisFT.Worker.dll --group "$2" ;;
  jobs)   shift; cd /app/jobs && exec dotnet KatalepsisFT.Jobs.dll "$@" ;;
  *)      echo "usage: run.sh web | worker <group> | jobs <command>" >&2; exit 2 ;;
esac
