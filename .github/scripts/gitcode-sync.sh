#!/usr/bin/env bash
# Push this repository's branches and tags to GitCode over HTTPS, so the tag a
# release is attached to always exists there before the release is mirrored.
#
# Authenticates with the GitCode personal access token (the same one the
# release API uses): GitCode accepts it as the password for HTTPS git, with
# the token owner's login as the username.
#
# Branches and tags are force-updated to match GitHub; refs that exist only on
# GitCode are left alone (nothing is deleted). The tags push is required; a
# rejected branch (for example a protected branch on GitCode) is reported as a
# warning so it does not block the release.
#
# README.md is the exception: GitCode keeps its own (Chinese) version. A
# branch whose README.md on GitCode differs from GitHub's is not overwritten.
# Instead GitHub's branch is merged into it, with a merge commit whose tree is
# GitHub's except for GitCode's README.md. Every GitHub commit and tag is
# still there unchanged; a tag's own snapshot has GitHub's README.md.
#
# Run from a full clone (actions/checkout with fetch-depth: 0), after the
# workflow's TCP tuning step so pushes get the same tuned connection.
#
# Required env: GITCODE_TOKEN, GITCODE_OWNER, GITCODE_REPO
# Optional env: TAG  check that this tag exists on GitCode afterwards
set -euo pipefail

remote="https://gitcode.com/${GITCODE_OWNER}/${GITCODE_REPO}.git"
# The file GitCode keeps its own version of.
KEEP_FILE=README.md

# retry <attempts> <command...>, waiting 15 s, 30 s, 45 s... between tries.
retry() {
  local n=$1 i
  shift
  for ((i = 1; ; i++)); do
    "$@" && return 0
    (( i >= n )) && return 1
    echo "'$1' failed; retrying in $((i * 15))s ($i/$n)" >&2
    sleep $((i * 15))
  done
}

# The token owner's login, used as the HTTPS git username.
gitcode_login() {
  curl -sS --fail --connect-timeout 30 --max-time 60 -G \
    https://api.gitcode.com/api/v5/user \
    --data-urlencode "access_token=$GITCODE_TOKEN" | jq -er '.login'
}
if ! GITCODE_USER=$(retry 4 gitcode_login); then
  echo "::error::Could not look up the GitCode account for the GITEEPAT token"
  exit 1
fi
export GITCODE_USER GITCODE_TOKEN
echo "Pushing as GitCode user $GITCODE_USER"

# git_gitcode <git args...>: run git against GitCode with the token supplied
# by a credential helper that reads the environment, so it never appears in a
# URL, the git config or the log.
git_gitcode() {
  git \
    -c credential.helper= \
    -c 'credential.helper=!f() { echo "username=$GITCODE_USER"; echo "password=$GITCODE_TOKEN"; }; f' \
    -c http.postBuffer=524288000 \
    -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=300 \
    "$@"
}

# Make sure every branch and tag from GitHub is present locally.
if [ "$(git rev-parse --is-shallow-repository)" = true ]; then
  retry 3 git fetch --unshallow --tags origin
fi
retry 3 git fetch --prune --tags origin '+refs/heads/*:refs/remotes/origin/*'
# origin/HEAD is a pointer, not a branch; keep it from becoming refs/heads/HEAD.
git remote set-head origin --delete 2>/dev/null || true

echo "Pushing tags to $remote"
if ! retry 3 git_gitcode push --force "$remote" 'refs/tags/*:refs/tags/*'; then
  echo "::error::Could not push tags to GitCode. Check that the GITEEPAT token has write access to $GITCODE_OWNER/$GITCODE_REPO, and that the repository is not a read-only pull mirror."
  exit 1
fi

# GitCode's branches, to find the ones carrying their own $KEEP_FILE. Without
# them a push could overwrite that file, so no branch is pushed if this fails.
echo "Fetching branches from $remote"
if ! retry 3 git_gitcode fetch --prune --no-tags "$remote" '+refs/heads/*:refs/remotes/gitcode/*'; then
  echo "::warning::Could not fetch GitCode's branches, so none were pushed (their $KEEP_FILE would be at risk); tags were synced."
else
  index=$(mktemp)
  refspecs=()
  while read -r branch; do
    src=refs/remotes/origin/$branch
    dst=refs/remotes/gitcode/$branch
    theirs=$(git rev-parse -q --verify "$dst:$KEEP_FILE" || true)
    ours=$(git rev-parse -q --verify "$src:$KEEP_FILE" || true)
    if [ -z "$theirs" ] || [ "$theirs" = "$ours" ] ||
       git merge-base --is-ancestor "$dst" "$src"; then
      # GitCode has no $KEEP_FILE of its own here: mirror the branch as it is.
      refspecs+=("+$src:refs/heads/$branch")
    elif git merge-base --is-ancestor "$src" "$dst"; then
      echo "Branch $branch: GitCode already has every commit"
    else
      # Merge GitHub's branch into GitCode's: GitHub's tree with GitCode's
      # $KEEP_FILE put back. GitCode's branch only moves forward, so this
      # one is pushed without forcing.
      mode=$(git ls-tree "$dst" -- "$KEEP_FILE" | cut -d' ' -f1)
      tree=$(
        export GIT_INDEX_FILE=$index
        git read-tree "$src"
        git update-index --add --cacheinfo "$mode,$theirs,$KEEP_FILE"
        git write-tree
      )
      merge=$(git \
        -c user.name='github-actions[bot]' \
        -c user.email='41898282+github-actions[bot]@users.noreply.github.com' \
        commit-tree "$tree" -p "$dst" -p "$src" \
        -m "Sync $branch from GitHub, keeping $KEEP_FILE")
      echo "Branch $branch: keeping GitCode's $KEEP_FILE (merge $merge)"
      refspecs+=("$merge:refs/heads/$branch")
    fi
  done < <(git for-each-ref --format='%(refname:strip=3)' refs/remotes/origin)
  rm -f "$index"

  if (( ${#refspecs[@]} )); then
    echo "Pushing branches to $remote"
    if ! retry 3 git_gitcode push "$remote" "${refspecs[@]}"; then
      echo "::warning::Some branches could not be pushed to GitCode (protected branch?); tags were synced."
    fi
  fi
fi

if [ -n "${TAG:-}" ]; then
  if retry 3 git_gitcode ls-remote --exit-code "$remote" "refs/tags/$TAG" >/dev/null; then
    echo "Tag $TAG is on GitCode"
  else
    echo "::error::Tag $TAG is still missing on GitCode after the push"
    exit 1
  fi
fi
