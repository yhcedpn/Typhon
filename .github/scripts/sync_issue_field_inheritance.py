#!/usr/bin/env python3
"""
Copy inheritable issue-level custom fields (default: Product) from a parent
issue to its sub-issues (Epic -> Feature -> Task).

GitHub has no built-in "inherit field from parent" automation (as of the
Issue Types / issue custom fields preview), so this script polls open issues
via GraphQL and backfills. See .github/workflows/sync-issue-field-inheritance.yml
for the schedule.

By default this only fills a field that is UNSET on the child - it never
overwrites a value someone deliberately set differently from the parent.
Set OVERWRITE_EXISTING=true to force strict inheritance instead.
"""
import json
import os
import sys
import urllib.error
import urllib.request

API_URL = "https://api.github.com/graphql"

ISSUES_QUERY = """
query($owner: String!, $name: String!, $cursor: String) {
  repository(owner: $owner, name: $name) {
    issues(first: 100, after: $cursor, states: OPEN) {
      pageInfo { hasNextPage endCursor }
      nodes {
        id
        number
        title
        parent {
          number
          issueFieldValues(first: 25) {
            nodes {
              ... on IssueFieldSingleSelectValue {
                optionId
                value
                field { ... on IssueFieldSingleSelect { id name } }
              }
            }
          }
        }
        issueFieldValues(first: 25) {
          nodes {
            ... on IssueFieldSingleSelectValue {
              optionId
              value
              field { ... on IssueFieldSingleSelect { id name } }
            }
          }
        }
      }
    }
  }
}
"""

SET_FIELD_MUTATION = """
mutation($issueId: ID!, $fieldId: ID!, $optionId: ID!) {
  setIssueFieldValue(input: {
    issueId: $issueId
    issueFields: [{ fieldId: $fieldId, singleSelectOptionId: $optionId }]
  }) {
    issueFieldValues {
      ... on IssueFieldSingleSelectValue { value }
    }
  }
}
"""


def gql(token, query, variables):
    req = urllib.request.Request(
        API_URL,
        data=json.dumps({"query": query, "variables": variables}).encode(),
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "Accept": "application/vnd.github+json",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req) as resp:
            body = json.loads(resp.read())
    except urllib.error.HTTPError as exc:
        raise RuntimeError(f"GraphQL request failed: {exc.code} {exc.read().decode()}") from exc
    if "errors" in body:
        raise RuntimeError(f"GraphQL errors: {json.dumps(body['errors'], indent=2)}")
    return body["data"]


def named_field_values(connection):
    """{'Product': {'fieldId': ..., 'optionId': ..., 'value': ...}, ...}"""
    out = {}
    for node in (connection or {}).get("nodes", []) or []:
        field = node.get("field") or {}
        name = field.get("name")
        if name:
            out[name] = {
                "fieldId": field.get("id"),
                "optionId": node.get("optionId"),
                "value": node.get("value"),
            }
    return out


def fetch_open_issues(token, owner, name):
    cursor = None
    while True:
        data = gql(token, ISSUES_QUERY, {"owner": owner, "name": name, "cursor": cursor})
        page = data["repository"]["issues"]
        yield from page["nodes"]
        if not page["pageInfo"]["hasNextPage"]:
            return
        cursor = page["pageInfo"]["endCursor"]


def main():
    token = os.environ["GH_TOKEN"]
    owner, name = os.environ["GH_REPO"].split("/")
    inherit_fields = [f.strip() for f in os.environ.get("INHERIT_FIELDS", "Product").split(",") if f.strip()]
    overwrite_existing = os.environ.get("OVERWRITE_EXISTING", "false").lower() == "true"
    dry_run = os.environ.get("DRY_RUN", "false").lower() == "true"

    updated = 0
    for issue in fetch_open_issues(token, owner, name):
        parent = issue.get("parent")
        if not parent:
            continue

        parent_fields = named_field_values(parent.get("issueFieldValues"))
        child_fields = named_field_values(issue.get("issueFieldValues"))

        for field_name in inherit_fields:
            parent_value = parent_fields.get(field_name)
            if not parent_value or not parent_value["optionId"]:
                continue

            child_value = child_fields.get(field_name)
            if child_value and child_value["optionId"] == parent_value["optionId"]:
                continue
            if child_value and not overwrite_existing:
                continue

            action = "would set" if dry_run else "setting"
            print(
                f"#{issue['number']} {issue['title']!r}: {action} {field_name} = "
                f"{parent_value['value']!r} (inherited from parent #{parent['number']})"
            )
            if dry_run:
                continue

            gql(
                token,
                SET_FIELD_MUTATION,
                {
                    "issueId": issue["id"],
                    "fieldId": parent_value["fieldId"],
                    "optionId": parent_value["optionId"],
                },
            )
            updated += 1

    print(f"Done. {updated} issue field value(s) updated.")


if __name__ == "__main__":
    main()
