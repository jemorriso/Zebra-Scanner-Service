# autoscan.py

# location and endpoint data has already been validated
# all that needs to be done is to connect to DB and update
# consider how I would add the functionality of multiple NIDs and locations. Eg from a file as well as just command line args
# for now just start with command line args

# add option to specify a file 
import argparse
import sys

import sqlite3
import subprocess

from datetime import datetime
import re

#leading characters of a barcode sometimes indicate device type
product_type_prefixes = {
    'T1' : ('TC-1116', '16'),
    'T2' : ('TC-1216', '3'),
    'T3' : ('TC-1120', '39'),
    'T4' : ('TC-1120-RD', '40'),
    'T5' : ('TC-1220', '41'),
    'T6' : ('TC-1220-RD', '42'),
    'TQ' : ('PP-1316', '12'),
    'X1' : ('XR-3100', '14'),
    # dummy ERT for now
    'ERT': ('ERT', '')
}

#attempts to connect to database
def setup_db():
    try:
    # 	#conn = sqlite3.connect('/var/www/inventory.db')
        conn = sqlite3.connect('/home/jmorrison/inventory-tester-environment/inventory_tester.db')
        #conn = sqlite3.connect('D:\Coding-Projects\inventory-tester-environment\inventory_tester.db')
        # cursor = conn.cursor()
        # cursor.execute("SELECT name from sqlite_master WHERE type='table';")
        # print(cursor.fetchall())
        print("Connected to database")
    except sqlite3.Error as e:
        print("Error connecting to database: {}".format(e))
        exit(1)

    return conn

def commit(db):
    try:
        db.commit()
        print('commit')
    except sqlite3.Error as e:
        print("Error committing to database: {}".format(e))
        exit(2)

#takes a socket barcode and parses it into a location, socket form and voltage
def parse_socket(barcode):
    
    location_data = {
        'location'			: '',
        'location_prefix' 	: '',
        'form'				: '',
        'voltage'			: '',
        'read_date'			: datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    }
    
    pillar_format = r'P[NESW][\d]{4}'
    # lab_format = r'Lab Stock Area'

    print(re.match(pillar_format, barcode))
    #if on a pillar - do I need others?
    if re.match(pillar_format, barcode):
        location_data['location'] = "{}{}{}".format(barcode[2:4],barcode[1],barcode[4:6])
        location_data['location_prefix'] = "P-"
        location_data['form'] = barcode[6:-3]
        location_data['voltage'] = barcode [-3:]

    # elif re.match(lab_format, barcode):
    #     location_data['location'] = "xxxx"
    #     location_data['location_prefix'] = "L-"

    else:
        print("Location type not recognized")
        # ***** this should never occur from barcode scanner that passes location ********
        exit(4)

    return location_data

# for now we won't use this method - can have multiple at location
def wipe_socket(db, location_data):
    ## should there be different behaviour for different locations?? EG programming lab

    #when a new location is scanned, set all devices previously at that location to location = None
    db.cursor().execute("UPDATE endpoints SET location='' WHERE location_prefix = ? AND location = ?", (location_data['location_prefix'], location_data['location']))

    commit(db)

def remove_endpoint(db, nid):
    db.cursor().execute("UPDATE endpoints SET location='' WHERE network_id=?", (nid,))
    commit(db)

def check_db_for_endpoint(db, nid):
    n_rows = db.cursor().execute("SELECT COUNT(*) FROM endpoints WHERE network_id=?", (nid,)).fetchone()[0]  # input needs to be tuple 
    return n_rows > 0

def check_db_for_comment(db, nid):

    #check if there is user/comment on nid
    query_result = db.cursor().execute("SELECT user, comment FROM endpoints WHERE network_id=?", (nid,)).fetchone()  # input needs to be tuple 

    print(query_result)

    # if there is a comment and/or user attached to the device return error code 3
    if query_result[0] or query_result[1]:
        print("User or comment")
        exit(3)

#adds endpoint network ID and socket data to the database
def add_endpoint(db, data, nid, nid_prefix, nid_in_db):
    print("nid prefix: {}".format(nid_prefix))

    query_variables = [data['location_prefix'], data['location'], data['read_date'], data['form'], data['voltage'], nid]

    #some barcodes have leading characters indicating product type/id, check for those
    if nid_prefix:
        product_name, product_id = product_type_prefixes.get(nid_prefix,('',''))
        query_variables.insert(-1, product_name)
        query_variables.insert(-1, product_id)


    #query_variables = [data['location_prefix'], data['location'], data['read_date'], data['form'], data['voltage'], product_name if product_name, product_id if product_id, nid]

    #if the nid exists, update it, if not, insert it
    if nid_in_db:
        if nid_prefix:
            query = "UPDATE endpoints SET location_prefix = ?, location = ?, read_date = ?, socket_form = ?, voltage = ?, product_name = ?, product_id = ? WHERE network_id = ?"
        else:
            query = "UPDATE endpoints SET location_prefix = ?, location = ?, read_date = ?, socket_form = ?, voltage = ? WHERE network_id = ?"

    else:
        if nid_prefix:
            query = "INSERT INTO endpoints (location_prefix, location, read_date, socket_form, voltage, product_name, product_id, network_id) VALUES (?,?,?,?,?,?,?,?)"
        else:
            query = "INSERT INTO endpoints (location_prefix, location, read_date, socket_form, voltage, network_id) VALUES (?,?,?,?,?,?)"


    
    print(query)
    # #run query
    db.cursor().execute(query, query_variables)
    	
    commit(db)

if __name__=="__main__":
    # establish db connection
    db = setup_db()

    endpoint = sys.argv[1]
    #chop the barcode so it's just the nid
    nid = endpoint[-10:]
    # Expect 12 digits for most products
    # 10 digits might be WAN NID, could also be ERT type+id
    # 8 digits is ERT
    if len(endpoint) == 12:
        nid_prefix = endpoint[:2] 
    elif len(endpoint) == 8:
        nid_prefix = 'ERT'
    else:
        nid_prefix = None

    print("nid prefix: {}".format(nid_prefix))

    nid_in_db = check_db_for_endpoint(db, nid)
    print("nid in db: {}".format(nid_in_db))

    # if removing won't have location parameter
    try:
        location = sys.argv[2]
        # # parse location into dictionary
        location_data = parse_socket(location)
        # if location holds multiple units, do not wipe socket
        # if (location_data['location_prefix'] == "P-"):
            # # wipe any entries for that socket
            # wipe_socket(db, location_data)    
        add_endpoint(db, location_data, nid, nid_prefix, nid_in_db)
    except IndexError:
        # if nid not in db, do nothing
        if nid_in_db:
            # make a better version using new schema
            #check_db_for_comment(db, nid)
            remove_endpoint(db, nid)
